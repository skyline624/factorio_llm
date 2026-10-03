using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Registers a native laboratory and its unclaimed supply pole without duplicating grid ownership.</summary>
internal static class FactoryLaboratoryAdoption
{
    public static FactoryCell? Plan(FactoryState state, FactorySnapshot stock, SpatialSnapshot map, ProductionCatalog catalog, string id)
    {
        if (state.WorldId != catalog.Scope.WorldId || stock.Scope != catalog.Scope || map.Scope != catalog.Scope)
            throw new InvalidDataException("Laboratory adoption observations have different actor scopes.");
        string name = catalog.Items["lab"].PlaceEntity ?? "lab";
        var lab = map.Entities.SingleOrDefault(e => e.Id == id && e.Name == name);
        var known = stock.Records.SingleOrDefault(r => r.Kind == "entity" && r.EntityId == id && r.Name == name
            && r.Data.GetProperty("role").GetString() == "factory" && r.Data.GetProperty("type").GetString() == "lab");
        var claimed = state.Cells.SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);
        if (lab is null || known is null || claimed.Contains(id) || FactoryPower.IsFed(stock, id) != true
            || lab.Power?.NetworkId is null || map.Prototypes[lab.Name].Type != "lab"
            || state.Cells.Any(c => c.Status == "building" && c.Plan?.Values.Any(p => p.Item == "lab"
                && p.Position.DistanceTo(lab.Position) < .01) == true)) return null;
        if (known.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!.DistanceTo(lab.Position) >= .01
            || known.Data.GetProperty("direction").GetInt32() != lab.Direction
            || known.Data.GetProperty("electricNetworkId").GetInt64() != lab.Power.NetworkId) return null;
        var entities = new Dictionary<string, string>(StringComparer.Ordinal) { ["machine"] = id };
        var plans = new Dictionary<string, PlannedEntity>(StringComparer.Ordinal)
            { ["machine"] = new("machine", "lab", lab.Position, lab.Direction) };
        // An existing cell retains its pole. A free native pole supplying the laboratory becomes its repairable part.
        var pole = map.Entities.Where(e => e.Force == lab.Force && !claimed.Contains(e.Id) && e.Power?.NetworkId == lab.Power.NetworkId
                && map.Prototypes[e.Name].Type == "electric-pole" && FactoryPower.IsFed(stock, e.Id) == true
                && stock.Records.Any(r => r.Kind == "entity" && r.EntityId == e.Id && r.Name == e.Name
                    && r.Data.GetProperty("role").GetString() == "factory")
                && PowerGridPlanner.Supplies(e.Position, map.Prototypes[e.Name], lab.Bounds))
            .OrderBy(e => e.Position.DistanceTo(lab.Position)).ThenBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault();
        if (pole is not null && catalog.Items.OrderBy(p => p.Key, StringComparer.Ordinal)
            .FirstOrDefault(p => p.Value.PlaceEntity == pole.Name).Key is { } poleItem)
        {
            entities["pole"] = pole.Id;
            plans["pole"] = new("pole", poleItem, pole.Position, pole.Direction);
        }
        return new($"lab-reused-{Guid.NewGuid():N}", 0, new(0, 0, true), "lab", "lab", null,
            entities, "ready", map.CollectedTick, Plan: plans);
    }
}
