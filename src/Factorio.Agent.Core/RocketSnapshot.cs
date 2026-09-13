using System.Text.Json;
using System.Text.Json.Serialization;

namespace Factorio.Agent.Core;

public sealed record RocketSiloPrototype(string EntityName, string Recipe, int PartsRequired, double CraftingSpeed, double EnergyPerTick);
public sealed record ObservedRocketSilo(string Id, string Name, MapPosition Position, string Recipe, int Parts, string Status,
    bool RocketPresent, bool InProcess, double Energy, IReadOnlyDictionary<string, long> Inputs,
    IReadOnlyDictionary<string, long> Insertable, long? NetworkId = null);
public sealed record RocketSnapshot(ActorScope Scope, long CollectedTick, int SurfaceIndex, long RocketsLaunched,
    bool Atomic, bool KnownSilosComplete, IReadOnlyDictionary<string, RocketSiloPrototype> Prototypes,
    [property: JsonConverter(typeof(NativeArrayConverter<ObservedRocketSilo>))] IReadOnlyList<ObservedRocketSilo> Silos)
{
    public static readonly IReadOnlySet<string> Statuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "building_rocket", "create_rocket", "lights_blinking_open", "doors_opening", "doors_opened", "rocket_rising",
        "arms_advance", "rocket_ready", "launch_starting", "launch_started", "engine_starting", "arms_retract",
        "rocket_flying", "lights_blinking_close", "doors_closing"
    };

    public static RocketSnapshot Parse(GameResponse response)
    {
        if (!response.Ok) throw new GameRpcException(response.Error!);
        var state = response.Data.Deserialize<RocketSnapshot>(Protocol.Json) ?? throw new InvalidDataException("Missing rocket observation.");
        if (state.Scope is null || string.IsNullOrWhiteSpace(state.Scope.WorldId) || string.IsNullOrWhiteSpace(state.Scope.SessionId)
            || string.IsNullOrWhiteSpace(state.Scope.ActorId) || state.Scope.Incarnation < 1 || state.Scope.Generation < 1
            || state.Silos is null || state.Prototypes is null
            || state.CollectedTick != response.Tick || state.CollectedTick < 0 || state.SurfaceIndex < 1
            || !state.Atomic || !state.KnownSilosComplete || state.RocketsLaunched < 0 || state.Silos.Count > 256
            || state.Prototypes.Values.Any(p => p is null || string.IsNullOrWhiteSpace(p.EntityName) || string.IsNullOrWhiteSpace(p.Recipe)
                || p.PartsRequired <= 0 || !Positive(p.CraftingSpeed) || !Positive(p.EnergyPerTick))
            || state.Silos.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id) || s.Position is null || s.Inputs is null || s.Insertable is null)
            || state.Silos.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != state.Silos.Count)
            throw new InvalidDataException("Incomplete or inconsistent rocket observation.");
        foreach (var silo in state.Silos)
        {
            var prototype = state.Prototypes.Values.FirstOrDefault(p => p.EntityName == silo.Name);
            if (prototype is null || silo.Parts < 0 || silo.Parts > prototype.PartsRequired || !Statuses.Contains(silo.Status)
                || silo.Recipe != prototype.Recipe || !double.IsFinite(silo.Energy) || silo.Energy < 0
                || !double.IsFinite(silo.Position.X) || !double.IsFinite(silo.Position.Y)
                || silo.Inputs.Values.Any(n => n < 0) || silo.Insertable.Values.Any(n => n < 0)
                || (silo.Status == "rocket_ready" && !silo.RocketPresent))
                throw new InvalidDataException("Invalid native silo state.");
        }
        return state;
        static bool Positive(double value) => double.IsFinite(value) && value > 0;
    }
}
