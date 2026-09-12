using System.Text.Json;
using System.Text.Json.Serialization;

namespace Factorio.Agent.Core;

public sealed record LaboratoryPrototype(string EntityName,
    [property: JsonConverter(typeof(NativeArrayConverter<string>))] IReadOnlyList<string> Inputs,
    double ResearchingSpeed, double EnergyPerTick);
public sealed record ObservedLaboratory(string Id, string Name, MapPosition Position, double Energy,
    IReadOnlyDictionary<string, long> Items, IReadOnlyDictionary<string, double> ScienceUnits,
    double ProductivityBonus, double SpeedBonus, long? NetworkId = null);
public sealed record ResearchSnapshot(ActorScope Scope, long CollectedTick, int SurfaceIndex, string Technology,
    bool Researched, double Progress, IReadOnlyDictionary<string, LaboratoryPrototype> Prototypes,
    [property: JsonConverter(typeof(NativeArrayConverter<ObservedLaboratory>))] IReadOnlyList<ObservedLaboratory> Labs,
    IReadOnlyDictionary<string, double> Consumed, bool KnownLabsComplete, bool Atomic,
    IReadOnlyDictionary<string, long> ActorItems, IReadOnlyDictionary<string, double> ActorScienceUnits, string? Selected = null)
{
    public static ResearchSnapshot Parse(GameResponse response, string expectedTechnology)
    {
        if (!response.Ok) throw new GameRpcException(response.Error!);
        var state = response.Data.Deserialize<ResearchSnapshot>(Protocol.Json)
            ?? throw new InvalidDataException("Missing native research snapshot.");
        if (state.Technology != expectedTechnology || state.CollectedTick != response.Tick || !state.Atomic || !state.KnownLabsComplete
            || !double.IsFinite(state.Progress) || state.Progress is < 0 or > 1
            || state.Labs.Count > 256 || state.Labs.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count() != state.Labs.Count
            || state.Prototypes.Values.Any(p => !double.IsFinite(p.ResearchingSpeed) || p.ResearchingSpeed <= 0
                || !double.IsFinite(p.EnergyPerTick) || p.EnergyPerTick <= 0)
            || state.Consumed.Values.Any(n => !double.IsFinite(n) || n < 0)
            || state.ActorItems.Values.Any(n => n < 0)
            || state.ActorScienceUnits.Any(p => !double.IsFinite(p.Value) || p.Value < 0 || p.Value > state.ActorItems.GetValueOrDefault(p.Key) + 1e-6)
            || state.Labs.Any(l => !double.IsFinite(l.Energy) || l.Energy < 0 || l.Items.Values.Any(n => n < 0)
                || l.ScienceUnits.Any(p => !double.IsFinite(p.Value) || p.Value < 0 || p.Value > l.Items.GetValueOrDefault(p.Key) + 1e-6)))
            throw new InvalidDataException("Incomplete or inconsistent native research observation.");
        return state;
    }
}
