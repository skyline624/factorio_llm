using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Checks the native graph, filters, local stock controls and powered endpoints in one known-factory photograph.</summary>
public static class FactoryTransportHealth
{
    public static IReadOnlySet<(string Chest, string Item)> Connected(FactoryState state, FactorySnapshot snapshot)
    {
        var result = new HashSet<(string, string)>();
        foreach (var bus in state.Transports ?? [])
            if (Healthy(state, snapshot, bus))
                foreach (var consumer in bus.Consumers)
                    result.Add((state.Cells.Single(c => c.Id == consumer.TargetCellId).Entities["input-chest"], bus.Item));
        return result;
    }

    public static bool Healthy(FactoryState state, FactorySnapshot snapshot, FactoryTransportBus bus)
    {
        if (snapshot.Scope.WorldId != state.WorldId) return false;
        var cell = state.Cells.SingleOrDefault(c => c.Id == bus.CellId);
        var sourceCell = state.Cells.SingleOrDefault(c => c.Id == bus.SourceCellId);
        if (cell is not { Status: "ready", Plan: not null } || sourceCell?.Status != "ready"
            || !sourceCell.Entities.TryGetValue("output-chest", out var sourceId) || bus.Consumers.Count == 0) return false;
        var records = snapshot.Records.Where(r => r.Kind == "entity").ToDictionary(r => r.EntityId, r => r.Data, StringComparer.Ordinal);
        var ordered = Belts(cell);
        if (ordered.Length == 0 || ordered.Any(role => !cell.Entities.ContainsKey(role))
            || cell.Entities.Values.Any(id => !records.ContainsKey(id)) || !records.TryGetValue(sourceId, out var source)) return false;
        string force = source.GetProperty("force").GetString()!;
        int surface = source.GetProperty("surfaceIndex").GetInt32();
        var beltIds = ordered.Select(role => cell.Entities[role]).ToArray();
        if (beltIds.Distinct().Count() != beltIds.Length || !cell.Entities.TryGetValue("source-inserter", out var sourceArm)) return false;
        var arms = new HashSet<string>(StringComparer.Ordinal) { sourceArm };
        if (!Arm(sourceArm, sourceId, beltIds[0], null, null)) return false;
        foreach (var consumer in bus.Consumers)
        {
            var target = state.Cells.SingleOrDefault(c => c.Id == consumer.TargetCellId);
            if (target?.Status != "ready" || !target.Entities.TryGetValue("input-chest", out var chest)
                || !records.TryGetValue(chest, out var targetData) || !Own(targetData)
                || !cell.Entities.TryGetValue(consumer.InserterRole, out var armId) || !arms.Add(armId)) return false;
            var native = Facts(records[armId]);
            if (native?.PickupTargetId is not { } pickup || !beltIds.Contains(pickup)
                || !Arm(armId, pickup, chest, chest, consumer.Paused ? 0 : consumer.Maximum)) return false;
            var chestCircuit = Facts(targetData);
            var declared = (state.Transports ?? []).SelectMany(b => b.Consumers.Select(c => (Bus: b, Consumer: c)))
                .Where(p => p.Consumer.TargetCellId == target.Id).Select(p => state.Cells.Single(c => c.Id == p.Bus.CellId)
                    .Entities.GetValueOrDefault(p.Consumer.InserterRole)).OfType<string>().ToHashSet(StringComparer.Ordinal);
            if (chestCircuit?.RedNeighbours is null || chestCircuit.RedNeighbourCount != chestCircuit.RedNeighbours.Count
                || chestCircuit.RedNeighbours.Any(id => !declared.Contains(id))) return false;
        }
        for (int i = 0; i < beltIds.Length; i++)
        {
            var data = records[beltIds[i]];
            var connections = Facts(data)?.BeltConnections;
            string[] input = i == 0 ? [] : [beltIds[i - 1]];
            string[] output = i + 1 == beltIds.Length ? [] : [beltIds[i + 1]];
            if (!Own(data) || data.GetProperty("type").GetString() != "transport-belt"
                || connections is null || connections.InputsCount != input.Length || connections.OutputsCount != output.Length
                || !connections.Inputs.SequenceEqual(input) || !connections.Outputs.SequenceEqual(output)) return false;
        }
        var belts = beltIds.ToHashSet(StringComparer.Ordinal);
        if (snapshot.Records.Where(r => r.Kind == "transit" && (belts.Contains(r.EntityId) || arms.Contains(r.EntityId)))
            .Any(r => r.Data.GetProperty("items").EnumerateObject().Any(p => p.Name != bus.Item && p.Value.GetInt64() > 0))) return false;
        // Native counts also include unknown neighbours; no unseen belt branch can be silently accepted.
        return !records.Any(pair => !arms.Contains(pair.Key) && Facts(pair.Value) is { } native
            && (native.PickupTargetId is { } pickup && (belts.Contains(pickup) || pickup == sourceId)
                || native.DropTargetId is { } drop && belts.Contains(drop)));

        bool Own(JsonElement data) => data.GetProperty("force").GetString() == force && data.GetProperty("surfaceIndex").GetInt32() == surface;
        bool Arm(string id, string pickup, string drop, string? chest, int? maximum)
        {
            if (!records.TryGetValue(id, out var data) || !Own(data) || data.GetProperty("type").GetString() != "inserter"
                || !data.TryGetProperty("power", out var power) || !power.TryGetProperty("networkId", out var network)
                || network.ValueKind != JsonValueKind.Number || FactoryPower.IsFed(snapshot, id) != true) return false;
            var native = Facts(data);
            return native?.PickupTargetId == pickup && native.DropTargetId == drop
                && FactoryTransportControl.Matches(native.InserterControl, bus.Item, chest, maximum);
        }
    }

    internal static string[] Belts(FactoryCell cell) => (cell.Plan?.Keys ?? []).Where(k => k.StartsWith("belt-", StringComparison.Ordinal))
        .OrderBy(k => int.Parse(k.AsSpan(5), System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static TransportFacts? Facts(JsonElement data) => data.TryGetProperty("transport", out var value) && value.ValueKind == JsonValueKind.Object
        ? value.Deserialize<TransportFacts>(Protocol.Json) : null;

    private sealed record TransportFacts(string? PickupTargetId = null, string? DropTargetId = null,
        ObservedBeltConnections? BeltConnections = null, ObservedInserterControl? InserterControl = null,
        [property: System.Text.Json.Serialization.JsonConverter(typeof(NativeArrayConverter<string>))] IReadOnlyList<string>? RedNeighbours = null,
        int? RedNeighbourCount = null, MapPosition? PickupPosition = null, MapPosition? DropPosition = null);
}
