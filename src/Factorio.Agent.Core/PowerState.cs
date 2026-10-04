using System.Text.Json;
using System.Text.Json.Serialization;

namespace Factorio.Agent.Core;

/// <summary>A producer on an electric network; energies are native joules per tick.</summary>
public sealed record PowerSource(string Id, string Name, string Type, MapPosition Position, double MaxPowerOutput,
    double? GeneratedLastTick = null, string? Status = null);
/// <summary>Consumers of one prototype on a network: summed native maximum usage and drain.</summary>
public sealed record PowerConsumers(string Name, int Count, double EnergyPerTick, double DrainPerTick);
/// <summary>Native electric statistics averaged over the named precision window.</summary>
public sealed record PowerFlow(string Precision, double Consumption, double Production);
public sealed record ElectricNetworkState(long NetworkId, int Poles,
    [property: JsonConverter(typeof(NativeArrayConverter<PowerSource>))] IReadOnlyList<PowerSource> Sources,
    [property: JsonConverter(typeof(NativeArrayConverter<PowerConsumers>))] IReadOnlyList<PowerConsumers> Consumers,
    PowerFlow? Statistics = null);
/// <summary>A known own boiler and the generators its steam reaches through direct connections and pipes.</summary>
public sealed record ObservedBoiler(string Id, string Name, MapPosition Position, int Direction, double EnergyPerTick, double Effectivity,
    IReadOnlyDictionary<string, long> Fuel,
    [property: JsonConverter(typeof(NativeArrayConverter<string>))] IReadOnlyList<string> GeneratorIds, string? Status = null,
    IReadOnlyDictionary<string, bool>? FuelCategories = null);

public sealed record PowerBudget(long NetworkId, double CapacityPerTick, double DemandPerTick)
{
    /// <summary>Expansion starts before steam runs out: above this share of capacity a new load would brown out soon.</summary>
    public const double Headroom = 0.8;

    public bool Exceeded(double additionalPerTick = 0) => DemandPerTick + additionalPerTick > Headroom * CapacityPerTick;
}

/// <summary>Read-only native reading of the known own electric networks and the boilers that feed them.</summary>
public sealed record PowerState(ActorScope Scope, long CollectedTick, int SurfaceIndex, int KnownEntityCount, bool Atomic,
    [property: JsonConverter(typeof(NativeArrayConverter<ElectricNetworkState>))] IReadOnlyList<ElectricNetworkState> Networks,
    [property: JsonConverter(typeof(NativeArrayConverter<ObservedBoiler>))] IReadOnlyList<ObservedBoiler> Boilers)
{
    public static PowerState Parse(GameResponse response)
    {
        if (!response.Ok) throw new GameRpcException(response.Error ?? new("invalid_response", "Power observation failed."));
        try
        {
            var state = response.Data.Deserialize<PowerState>(Protocol.Json) ?? throw new InvalidDataException("Missing power observation.");
            var sources = state.Networks.SelectMany(n => n.Sources).ToArray();
            if (state.CollectedTick != response.Tick || !state.Atomic || state.Networks.Count > 64 || state.KnownEntityCount < 0
                || state.Networks.Select(n => n.NetworkId).Distinct().Count() != state.Networks.Count
                || sources.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != sources.Length
                || state.Boilers.Select(b => b.Id).Distinct(StringComparer.Ordinal).Count() != state.Boilers.Count
                || state.Networks.Any(n => n.Poles < 0 || n.Statistics is { } flow && !(Energy(flow.Consumption) && Energy(flow.Production)))
                || sources.Any(s => !Energy(s.MaxPowerOutput) || s.GeneratedLastTick is { } generated && !Energy(generated))
                || state.Networks.SelectMany(n => n.Consumers).Any(c => c.Count < 1 || !Energy(c.EnergyPerTick) || !Energy(c.DrainPerTick))
                || state.Boilers.Any(b => !Energy(b.EnergyPerTick) || b.EnergyPerTick == 0 || !Energy(b.Effectivity) || b.Effectivity == 0
                    || b.Fuel.Values.Any(n => n < 0) || b.GeneratorIds.Any(string.IsNullOrWhiteSpace)))
                throw new InvalidDataException("Incomplete or inconsistent native power observation.");
            return state;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException("Invalid native power data.", error);
        }

        static bool Energy(double value) => double.IsFinite(value) && value >= 0;
    }

    /// <summary>
    /// Capacity per network: boilers and the generators they feed form steam groups bounded by both heat and engine output.
    /// A generator without an observed boiler has no steam supply; other sources count their native maximum output.
    /// </summary>
    public PowerBudget Budget(ElectricNetworkState network)
    {
        var generators = network.Sources.Where(s => s.Type == "generator").ToDictionary(s => s.Id, StringComparer.Ordinal);
        var groups = new List<(double Heat, HashSet<string> Engines)>();
        foreach (var boiler in Boilers)
        {
            var fed = boiler.GeneratorIds.Where(generators.ContainsKey).ToHashSet(StringComparer.Ordinal);
            if (fed.Count == 0) continue;
            double heat = boiler.EnergyPerTick * boiler.Effectivity;
            foreach (var joined in groups.Where(g => g.Engines.Overlaps(fed)).ToArray())
            {
                heat += joined.Heat;
                fed.UnionWith(joined.Engines);
                groups.Remove(joined);
            }
            groups.Add((heat, fed));
        }
        double steam = groups.Sum(g => Math.Min(g.Heat, g.Engines.Sum(id => generators[id].MaxPowerOutput)));
        double other = network.Sources.Where(s => s.Type != "generator").Sum(s => s.MaxPowerOutput);
        return new(network.NetworkId, steam + other, network.Consumers.Sum(c => c.EnergyPerTick + c.DrainPerTick));
    }

    /// <summary>The powered network with the largest capacity, where the factory grows.</summary>
    public ElectricNetworkState? Main() => Networks.Where(n => n.Sources.Count > 0)
        .OrderByDescending(n => Budget(n).CapacityPerTick).ThenBy(n => n.NetworkId).FirstOrDefault();
}
