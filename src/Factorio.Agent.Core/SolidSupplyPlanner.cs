namespace Factorio.Agent.Core;

public sealed record SolidSupplyCandidate(string SourceId, bool Existing, long Available, double Distance, bool Producing = false, bool InputsAvailable = false);

/// <summary>Selects observed, unambiguous material supplies; installation and delivery remain separate actions.</summary>
public sealed class SolidSupplyPlanner
{
    public IReadOnlyList<SolidSupplyCandidate> Candidates(SpatialSnapshot map, FactorySnapshot stock, ProductionCatalog catalog,
        string targetId, string item, IReadOnlySet<string>? reservedEntityIds = null)
        => CandidatesCore(map, stock, catalog, targetId, item, reservedEntityIds, true);

    private IReadOnlyList<SolidSupplyCandidate> CandidatesCore(SpatialSnapshot map, FactorySnapshot stock, ProductionCatalog catalog,
        string targetId, string item, IReadOnlySet<string>? reservedEntityIds, bool inspectInputs)
    {
        if (map.Scope != stock.Scope || map.Scope != catalog.Scope) throw new InvalidDataException("Solid supply observations span different scopes.");
        stock.SummarizeStocks();
        var targetEntity = map.Entities.Single(e => e.Id == targetId);
        var target = MaterialEndpoint.From(stock, catalog, targetId, item, false);
        var result = new List<SolidSupplyCandidate>();
        foreach (var entity in map.Entities.Where(e => e.Id != targetId && reservedEntityIds?.Contains(e.Id) != true && e.Force == targetEntity.Force
            && map.Prototypes[e.Name].Type is "container" or "assembling-machine" or "furnace"))
        {
            var source = MaterialEndpoint.TryFrom(stock, catalog, entity.Id, item, true);
            if (source is null) continue;
            var inventory = stock.Records.Single(r => r.Id == source.InventoryId);
            if (inventory.Data.GetProperty("items").EnumerateObject().Any(p => p.Name != item && p.Value.GetInt64() > 0)) continue;
            var line = BeltTransportNetwork.FindSegment(map, entity.Id, targetId);
            if (line is null && map.Entities.Any(e => e.PickupTargetId == entity.Id
                || (e.PickupPosition is not null && entity.Bounds.Contains(e.PickupPosition)))) continue;
            var boundary = BeltTransportBoundary.TryFrom(map, stock, catalog, entity.Id, targetId, item);
            if (boundary is null) continue;
            if (reservedEntityIds is not null && boundary.ReservedEntityIds.Any(reservedEntityIds.Contains)) continue;
            // An empty finite root does not identify the material of another ingredient's occupied line.
            if (boundary.Root.Recipe is null && boundary.Root.Read(stock, item).Count == 0)
            {
                var ids = boundary.ReservedEntityIds.Concat(line?.BeltIds ?? []).ToHashSet(StringComparer.Ordinal);
                if (line is not null) { ids.Add(line.SourceInserterId); ids.Add(line.TargetInserterId); }
                bool carriesItem = stock.Records.Where(r => ids.Contains(r.EntityId) && r.Kind is "inventory" or "transit")
                    .Any(r => r.Data.GetProperty("items").TryGetProperty(item, out var n) && n.GetInt64() > 0);
                if (!carriesItem) continue;
            }
            var reading = boundary.Read(stock, target, item, line?.BeltIds ?? [],
                line is null ? [] : [line.SourceInserterId, line.TargetInserterId]);
            long available = checked(reading.Source.Count + reading.Transit);
            bool supplied = inspectInputs && available == 0 && !reading.Source.InProcess && HasConnectedInputs(map, stock, catalog, boundary.Root);
            if (available > 0 || reading.Source.InProcess || supplied)
                result.Add(new(entity.Id, line is not null, available, entity.Position.DistanceTo(targetEntity.Position), reading.Source.InProcess, supplied));
        }
        return result.OrderByDescending(s => s.Existing).ThenBy(s => s.Distance).ThenBy(s => s.SourceId, StringComparer.Ordinal).ToArray();
    }

    public bool HasConnectedInputs(SpatialSnapshot map, FactorySnapshot stock, ProductionCatalog catalog, MaterialEndpoint root)
    {
        if (root.Recipe is null) return false;
        var machine = map.Entities.Single(e => e.Id == root.EntityId);
        if (!map.Prototypes[machine.Name].IsElectric || machine.Power is not { NetworkId: not null, Energy: > 0 }) return false;
        var recipe = catalog.Recipes.Single(r => r.Name == root.Recipe);
        var required = MachineInputRequirements.From(stock, root.EntityId, recipe, 1);
        if (required.Fluids.Any(p => p.Value > 0)) return false;
        foreach (var ingredient in required.Items.Where(p => p.Value > 0))
        {
            // Only material already on an existing incoming connection counts; never recursively invent output.
            var sources = CandidatesCore(map, stock, catalog, root.EntityId, ingredient.Key, null, false);
            if (!sources.Any(s => s.Existing && s.Available >= ingredient.Value)) return false;
        }
        return true;
    }
}
