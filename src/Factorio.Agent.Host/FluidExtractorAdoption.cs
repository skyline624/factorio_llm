using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Registers a proven free native extractor and its unclaimed power links without constructing another drill.</summary>
internal static class FluidExtractorAdoption
{
    internal const int MaximumCandidates = 16, MaximumPowerLinks = 128;

    public static IReadOnlyList<FactoryRecord> Candidates(FactoryState state, FactorySnapshot snapshot, string fluid) =>
        snapshot.Records.Where(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "factory"
            && r.Data.GetProperty("type").GetString() == "mining-drill"
            && !state.Cells.Any(c => c.Entities.Values.Contains(r.EntityId))
            && FactoryPower.IsFed(snapshot, r.EntityId) == true && snapshot.FluidStockAt(r.EntityId, fluid) > 0).ToArray();

    public static FactoryCell? Plan(FactoryState state, FactorySnapshot snapshot, SpatialSnapshot map,
        ProductionCatalog catalog, string fluid, string drillId)
    {
        if (state.WorldId != catalog.Scope.WorldId || snapshot.Scope != catalog.Scope || map.Scope != catalog.Scope)
            throw new InvalidDataException("Extractor adoption observations span different actor scopes.");
        if (!map.Coverage.Atomic || !map.Coverage.Complete || map.Actor.ControlMode != "ai") return null;
        var owned = Candidates(state, snapshot, fluid).SingleOrDefault(r => r.EntityId == drillId);
        var drill = map.Entities.SingleOrDefault(e => e.Id == drillId);
        if (owned is null || drill is null || owned.Name != drill.Name
            || owned.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!.DistanceTo(drill.Position) > .01
            || drill.Power is not { Energy: > 0, NetworkId: { } network }
            || !owned.Data.TryGetProperty("electricNetworkId", out var nativeNetwork) || nativeNetwork.GetInt64() != network
            || !map.Prototypes.TryGetValue(drill.Name, out var prototype) || prototype is not { Type: "mining-drill", IsElectric: true }
            || prototype.FluidBoxes?.Any(b => b.ProductionType is "output" or "input-output") != true
            || drill.FluidConnections is not { Count: > 0 } ports
            || !ports.Any(p => p.FlowDirection is "output" or "input-output")
            || ports.Any(p => p.TargetEntityId is not null || p.TargetBoxIndex is not null)) return null;
        string? item = catalog.Items.Where(p => p.Value.PlaceEntity == drill.Name && p.Value.PlaceEntityType == "mining-drill")
            .Select(p => p.Key).Order(StringComparer.Ordinal).FirstOrDefault();
        if (item is null) return null;
        var cell = new FactoryCell($"fluid-{Guid.NewGuid():N}", 0, new(0, 0, true), FluidCellBuilder.ExtractorKind, item, fluid,
            new Dictionary<string, string> { ["drill"] = drillId }, "ready", snapshot.CollectedTick);
        if (FluidCellBuilder.Rate(map, catalog, cell) is not > 0) return null;
        // The resource-trigger executor did not register its grid links. Keep the still-unclaimed owned poles on
        // this native network, including links from an interrupted attempt, so upkeep can rebuild them too.
        var claimed = state.Cells.SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);
        var poles = snapshot.Records.Where(r => r.Kind == "entity" && !claimed.Contains(r.EntityId)
                && r.Data.GetProperty("role").GetString() == "factory" && r.Data.GetProperty("type").GetString() == "electric-pole"
                && r.Data.TryGetProperty("electricNetworkId", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt64() == network)
            .OrderByDescending(r => r.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!.DistanceTo(drill.Position))
            .ThenBy(r => r.EntityId, StringComparer.Ordinal).Take(MaximumPowerLinks + 1).ToArray();
        if (poles.Length > MaximumPowerLinks) throw new InvalidOperationException("Extractor adoption exceeds its native power-link budget.");
        var entities = new Dictionary<string, string>(cell.Entities, StringComparer.Ordinal);
        for (int i = 0; i < poles.Length; i++) entities[$"link-{i}"] = poles[i].EntityId;
        cell = cell with { Entities = entities };
        return FactoryMaintenance.RecoverPlans(state.With(cell), snapshot, catalog).SingleOrDefault(c => c.Id == cell.Id);
    }
}
