namespace Factorio.Agent.Core;

public sealed record FluidSupplyRoute(string SourceId, PipeRoutePlan Route);

public sealed class FluidSupplyPlanner
{
    public FluidSupplyRoute? Find(SpatialSnapshot map, FactorySnapshot stock, string pipeItem, string targetId, string fluid,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (map.Scope != stock.Scope) throw new InvalidDataException("Fluid supply observations have different actor scopes.");
        var target = map.Entities.Single(e => e.Id == targetId);
        stock.SummarizeStocks();
        var available = new Dictionary<string, double>(StringComparer.Ordinal);
        var boxes = new Dictionary<(string Id, int Box), double>();
        foreach (var record in stock.Records.Where(r => r.Kind == "fluid"))
        {
            if (!record.Data.GetProperty("contents").TryGetProperty(fluid, out var value) || value.GetDouble() <= 0) continue;
            double amount = value.GetDouble();
            var owners = new HashSet<string>(StringComparer.Ordinal) { record.EntityId };
            var ownedBoxes = new HashSet<(string Id, int Box)>();
            if (record.Data.TryGetProperty("sourceBoxes", out var sourceBoxes) && sourceBoxes.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var box in sourceBoxes.EnumerateArray())
                {
                    string id = box.GetProperty("entityId").GetString()!;
                    owners.Add(id);
                    if (box.TryGetProperty("index", out var index)) ownedBoxes.Add((id, index.GetInt32()));
                }
            foreach (string id in owners) available[id] = available.GetValueOrDefault(id) + amount;
            foreach (var box in ownedBoxes) boxes[box] = boxes.GetValueOrDefault(box) + amount;
        }
        foreach (var source in map.Entities.Where(e => e.Id != targetId && e.Force == target.Force
            && (e.FluidConnections ?? []).Any(p => p.Type == "normal" && (p.Filter is null || p.Filter == fluid)
                && p.FlowDirection is "output" or "input-output")
            && (available.GetValueOrDefault(e.Id) > 0 || OffshoreSupplyPlanner.CanExtract(map, e.Id, fluid)))
            .OrderBy(e => e.Position.DistanceTo(target.Position)).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var route = new PipeRoutePlanner().Find(map, pipeItem, source.Id, targetId, fluid, cancellationToken: cancellationToken);
            if (route.Status == PipeRouteStatus.Found && route.Source is { } output
                && (boxes.GetValueOrDefault((source.Id, output.BoxIndex)) > 0 || OffshoreSupplyPlanner.CanExtract(map, source.Id, fluid)))
                return new(source.Id, route);
        }
        return null;
    }
}
