using System.Text.Json;

namespace Factorio.Agent.Core;

/// <summary>A validated, current projection of engine facts needed by the reflex loop.</summary>
public sealed record SafetyObservation(long Tick, ActorScope Scope, bool Alive, string ControlMode,
    bool StopUnconfirmed, MapPosition? Position, double Health, WeaponState Weapon,
    IReadOnlyList<VisibleThreat> Enemies, OperationReceipt? Operation, EquipmentState? Loadout = null,
    double? MaxHealth = null, IReadOnlyList<DefensiveRefuge>? Defenses = null, bool LocalEnemiesComplete = false)
{
    public static SafetyObservation Parse(GameResponse response)
    {
        if (!response.Ok) throw new GameRpcException(response.Error ?? new("invalid_response", "Observation failed without an error."));
        try
        {
            JsonElement data = response.Data;
            long tick = data.GetProperty("collectedTick").GetInt64();
            JsonElement coverage = data.GetProperty("coverage");
            if (tick < 0 || tick != response.Tick || !coverage.GetProperty("atomic").GetBoolean()
                || coverage.GetProperty("collectionStartTick").GetInt64() != tick
                || coverage.GetProperty("collectionEndTick").GetInt64() != tick
                || coverage.GetProperty("enemyVisibility").GetString() != "normal-character-5x5-chunks-or-native-current-visibility")
                throw new InvalidDataException("Defense requires a current atomic observation with normal enemy visibility.");
            ActorScope scope = data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)
                ?? throw new InvalidDataException("Missing actor scope.");
            if (string.IsNullOrWhiteSpace(scope.WorldId) || string.IsNullOrWhiteSpace(scope.SessionId)
                || string.IsNullOrWhiteSpace(scope.ActorId) || scope.Incarnation < 0 || scope.Generation < 0)
                throw new InvalidDataException("Invalid actor scope.");
            JsonElement agent = data.GetProperty("agent");
            bool alive = agent.GetProperty("alive").GetBoolean();
            string mode = agent.GetProperty("controlMode").GetString() ?? "";
            if (mode is not ("ai" or "manual")) throw new InvalidDataException("Unknown control mode.");
            MapPosition? position = alive ? ReadPosition(agent.GetProperty("position")) : null;
            double health = alive ? Finite(agent.GetProperty("health")) : 0;
            if (health < 0) throw new InvalidDataException("Negative actor health.");
            double? maxHealth = alive && agent.TryGetProperty("maxHealth", out var maximum) ? Finite(maximum) : null;
            if (maxHealth is <= 0 || maxHealth is not null && health > maxHealth) throw new InvalidDataException("Invalid native maximum health.");
            WeaponState weapon = WeaponState.Unavailable;
            if (alive)
            {
                JsonElement node = agent.GetProperty("weapon");
                bool ready = node.GetProperty("ready").GetBoolean();
                int rounds = node.GetProperty("rounds").GetInt32();
                double range = ready ? Finite(node.GetProperty("range")) : 0;
                if (rounds < 0 || (ready && (rounds == 0 || range <= 0)))
                    throw new InvalidDataException("Invalid weapon capacity or range.");
                weapon = new(ready, rounds, range);
            }
            List<VisibleThreat> enemies = [];
            JsonElement nodes = data.GetProperty("enemies");
            if (nodes.ValueKind == JsonValueKind.Array)
            {
                HashSet<string> ids = [];
                foreach (JsonElement node in nodes.EnumerateArray())
                {
                    string id = node.GetProperty("id").GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(id) || !ids.Add(id) || node.GetProperty("collectedTick").GetInt64() != tick)
                        throw new InvalidDataException("Enemy identity or observation freshness is invalid.");
                    enemies.Add(new(id, ReadPosition(node.GetProperty("position"))));
                }
            }
            else if (nodes.ValueKind != JsonValueKind.Object || nodes.EnumerateObject().Any())
                throw new InvalidDataException("Invalid enemy collection.");
            OperationReceipt? operation = null;
            if (data.TryGetProperty("operation", out JsonElement receipt))
            {
                operation = OperationReceipt.Parse(receipt, receipt.GetProperty("operationId").GetString()!);
                if (operation.UpdatedTick > tick) throw new InvalidDataException("Operation is newer than its observation.");
            }
            return new(tick, scope, alive, mode, agent.GetProperty("stopUnconfirmed").GetBoolean(),
                position, health, weapon, enemies.AsReadOnly(), operation,
                alive && agent.TryGetProperty("loadout", out var loadout) && loadout.ValueKind != JsonValueKind.Null
                    ? EquipmentState.Parse(loadout) : null, maxHealth,
                data.TryGetProperty("defenses", out var defenses) ? DefensiveRefuge.Read(defenses, tick) : [],
                coverage.TryGetProperty("enemiesTruncated", out var truncated) && !truncated.GetBoolean());
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Invalid native safety observation; no action may be inferred from it.", error);
        }
    }

    private static MapPosition ReadPosition(JsonElement node) => new(Finite(node.GetProperty("x")), Finite(node.GetProperty("y")));
    private static double Finite(JsonElement node)
    {
        double value = node.GetDouble();
        return double.IsFinite(value) ? value : throw new InvalidDataException("Non-finite engine measurement.");
    }
}

public sealed record WeaponState(bool Ready, int Rounds, double Range)
{
    public static WeaponState Unavailable { get; } = new(false, 0, 0);
}

public sealed record VisibleThreat(string Id, MapPosition Position);

public static class DefensePolicy
{
    public static VisibleThreat? SelectTarget(SafetyObservation observation)
    {
        if (!observation.Alive || observation.ControlMode != "ai" || observation.StopUnconfirmed
            || observation.Position is null || !observation.Weapon.Ready || observation.Health <= 0)
            return null;
        return observation.Enemies
            .Where(e => observation.Position.DistanceTo(e.Position) <= observation.Weapon.Range)
            .OrderBy(e => observation.Position.DistanceTo(e.Position))
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
