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
        if (snapshot.Scope.WorldId != state.WorldId || bus.PendingRetirements is { Count: > 0 }) return false;
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
        if (bus.ActorReserve is < 0 or > 10000
            || !Arm(sourceArm, sourceId, beltIds[0], bus.ActorReserve is null ? null : sourceId, bus.ActorReserve,
                bus.ActorReserve is null ? "<" : ">")) return false;
        if (bus.ActorReserve is not null)
        {
            var sourceCircuit = Facts(source);
            if (sourceCircuit?.RedNeighbourCount != 1 || sourceCircuit.RedNeighbours?.SequenceEqual([sourceArm]) != true) return false;
        }
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
            var facts = Facts(data);
            var connections = facts?.BeltConnections;
            if (bus.Graph is not null) continue;
            var planned = cell.Plan[ordered[i]];
            bool underground = planned.UndergroundType is not null;
            string[] input = i == 0 || planned.UndergroundType == "output" ? [] : [beltIds[i - 1]];
            string[] output = i + 1 == beltIds.Length || planned.UndergroundType == "input" ? [] : [beltIds[i + 1]];
            if (!Own(data) || data.GetProperty("type").GetString() != (underground ? "underground-belt" : "transport-belt")
                || connections is null || connections.InputsCount != input.Length || connections.OutputsCount != output.Length
                || !connections.Inputs.SequenceEqual(input) || !connections.Outputs.SequenceEqual(output)) return false;
            if (underground)
            {
                int other = i + (planned.UndergroundType == "input" ? 1 : -1);
                if (planned.UndergroundType is not ("input" or "output") || other < 0 || other >= beltIds.Length
                    || facts?.Underground is not { NeighbourCount: 1 } native || native.Type != planned.UndergroundType
                    || native.NeighbourId != beltIds[other]
                    || cell.Plan[ordered[other]].UndergroundType != (planned.UndergroundType == "input" ? "output" : "input")
                    || cell.Plan[ordered[other]].Direction != planned.Direction || cell.Plan[ordered[other]].Item != planned.Item
                    || !data.TryGetProperty("position", out var position) || position.Deserialize<MapPosition>(Protocol.Json) != planned.Position
                    || !data.TryGetProperty("direction", out var direction) || direction.GetInt32() != planned.Direction) return false;
                var step = ExtractionPlanner.Rotate(new(0, planned.UndergroundType == "input" ? -1 : 1), planned.Direction);
                var partner = cell.Plan[ordered[other]].Position;
                double separation = Math.Abs(planned.Position.X - partner.X) + Math.Abs(planned.Position.Y - partner.Y);
                if (separation < 2 || partner != new MapPosition(planned.Position.X + step.X * separation, planned.Position.Y + step.Y * separation)) return false;
                try { BeltTransportReading.TransportRecords(snapshot, beltIds[i], false); }
                catch (InvalidDataException) { return false; }
            }
        }
        if (bus.Graph is not null && !GraphMatches()) return false;
        var belts = (bus.Graph?.Keys.Select(role => cell.Entities[role]) ?? beltIds).ToHashSet(StringComparer.Ordinal);
        if (snapshot.Records.Where(r => r.Kind == "transit" && (belts.Contains(r.EntityId) || arms.Contains(r.EntityId)))
            .Any(r => r.Data.GetProperty("items").EnumerateObject().Any(p => p.Name != bus.Item && p.Value.GetInt64() > 0))) return false;
        // Native counts also include unknown neighbours; no unseen belt branch can be silently accepted.
        return !records.Any(pair => !arms.Contains(pair.Key) && Facts(pair.Value) is { } native
            && (native.PickupTargetId is { } pickup && (belts.Contains(pickup) || pickup == sourceId)
                || native.DropTargetId is { } drop && belts.Contains(drop)));

        bool Own(JsonElement data) => data.GetProperty("force").GetString() == force && data.GetProperty("surfaceIndex").GetInt32() == surface;
        bool GraphMatches()
        {
            var graph = bus.Graph!;
            var roles = cell.Plan!.Keys.Where(r => r.StartsWith("belt-", StringComparison.Ordinal) || r.StartsWith("splitter-", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
            if (!roles.SetEquals(graph.Keys) || graph.Count > 208 || !graph.Keys.Any(r => r.StartsWith("splitter-", StringComparison.Ordinal))) return false;
            if (graph.Keys.Any(r => !cell.Entities.ContainsKey(r)) || graph.Keys.Select(r => cell.Entities[r]).Distinct().Count() != graph.Count) return false;
            foreach (var (role, edges) in graph)
            {
                if (!cell.Entities.TryGetValue(role, out var id) || !records.TryGetValue(id, out var data) || !Own(data)
                    || edges.Inputs.Distinct().Count() != edges.Inputs.Count || edges.Outputs.Distinct().Count() != edges.Outputs.Count
                    || edges.Inputs.Concat(edges.Outputs).Any(r => r == role || !graph.ContainsKey(r))) return false;
                foreach (var other in edges.Inputs) if (!graph[other].Outputs.Contains(role)) return false;
                foreach (var other in edges.Outputs) if (!graph[other].Inputs.Contains(role)) return false;
                var native = Facts(data);
                bool split = role.StartsWith("splitter-", StringComparison.Ordinal);
                if (data.GetProperty("type").GetString() != (split ? "splitter" : "transport-belt")
                    || split && native?.SplitterControl is not { InputPriority: "none", OutputPriority: "none", Filter: null }
                    || edges.Inputs.Count > (split ? 2 : 1) || edges.Outputs.Count > (split ? 2 : 1)
                    || native?.BeltConnections is not { } connections
                    || connections.InputsCount != edges.Inputs.Count || connections.OutputsCount != edges.Outputs.Count
                    || !connections.Inputs.SequenceEqual(edges.Inputs.Select(r => cell.Entities[r]).Order(StringComparer.Ordinal))
                    || !connections.Outputs.SequenceEqual(edges.Outputs.Select(r => cell.Entities[r]).Order(StringComparer.Ordinal))) return false;
            }
            var terminals = graph.Where(p => p.Value.Outputs.Count == 0).Select(p => cell.Entities[p.Key]).ToHashSet(StringComparer.Ordinal);
            var pickups = bus.Consumers.Select(c => Facts(records[cell.Entities[c.InserterRole]])?.PickupTargetId).OfType<string>().ToArray();
            if (pickups.Length != bus.Consumers.Count || pickups.Distinct().Count() != pickups.Length || !terminals.SetEquals(pickups)) return false;
            // Every declared conveyor must be reachable from the source, and the plan must be acyclic.
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            bool Visit(string role)
            {
                if (visiting.Contains(role)) return false;
                if (visited.Contains(role)) return true;
                visiting.Add(role);
                foreach (var next in graph[role].Outputs) if (!Visit(next)) return false;
                visiting.Remove(role); visited.Add(role); return true;
            }
            return graph.TryGetValue(ordered[0], out var root) && root.Inputs.Count == 0 && Visit(ordered[0]) && visited.Count == graph.Count;
        }
        bool Arm(string id, string pickup, string drop, string? chest, int? maximum, string comparator = "<")
        {
            if (!records.TryGetValue(id, out var data) || !Own(data) || data.GetProperty("type").GetString() != "inserter"
                || !data.TryGetProperty("power", out var power) || !power.TryGetProperty("networkId", out var network)
                || network.ValueKind != JsonValueKind.Number || FactoryPower.IsFed(snapshot, id) != true) return false;
            var native = Facts(data);
            return native?.PickupTargetId == pickup && native.DropTargetId == drop
                && FactoryTransportControl.Matches(native.InserterControl, bus.Item, chest, maximum, comparator);
        }
    }

    internal static string[] Belts(FactoryCell cell) => (cell.Plan?.Keys ?? []).Where(k => k.StartsWith("belt-", StringComparison.Ordinal))
        .OrderBy(k => int.Parse(k.AsSpan(5), System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static TransportFacts? Facts(JsonElement data) => data.TryGetProperty("transport", out var value) && value.ValueKind == JsonValueKind.Object
        ? value.Deserialize<TransportFacts>(Protocol.Json) : null;

    private sealed record TransportFacts(string? PickupTargetId = null, string? DropTargetId = null,
        ObservedBeltConnections? BeltConnections = null, ObservedInserterControl? InserterControl = null,
        [property: System.Text.Json.Serialization.JsonConverter(typeof(NativeArrayConverter<string>))] IReadOnlyList<string>? RedNeighbours = null,
        int? RedNeighbourCount = null, MapPosition? PickupPosition = null, MapPosition? DropPosition = null,
        ObservedSplitterControl? SplitterControl = null,
        [property: System.Text.Json.Serialization.JsonConverter(typeof(NativeArrayConverter<string>))] IReadOnlyList<string>? GreenNeighbours = null,
        int? GreenNeighbourCount = null, ObservedUndergroundBelt? Underground = null);
}
