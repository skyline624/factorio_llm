namespace Factorio.Agent.Core;

public sealed record ConfiguredMachinePlacement(PlacementCandidate Placement, string PoleId,
    MapPosition BuildApproach, IReadOnlyList<PlannedFluidSupply> Supplies);

/// <summary>Uses an observed configured machine as geometry evidence; recipe port identities are never guessed from prototypes.</summary>
public sealed class ConfiguredMachinePlacementPlanner
{
    public const string PlannedId = "planned:configured-machine";

    public ConfiguredMachinePlacement? Find(SpatialSnapshot map, FactorySnapshot stock, string entityId,
        string machineItem, string pipeItem, IReadOnlyList<string> fluids, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (map.Scope != stock.Scope) throw new InvalidDataException("Configured placement requires one actor scope.");
        var original = map.Entities.Single(e => e.Id == entityId);
        if (original.Name != map.Items[machineItem].EntityName || original.FluidConnections is not { Count: > 0 })
            throw new InvalidDataException("Placement requires the matching native configured machine geometry.");
        var geometry = map.Prototypes[original.Name];
        var vacant = map with { Entities = map.Entities.Where(e => e.Id != entityId && e.Id != map.Actor.Id).ToArray() };
        var field = new SpatialCollisionField(vacant);
        var placement = new PlacementPlanner();
        foreach (var pole in vacant.Entities.Where(e => e.Force == original.Force && e.Power?.NetworkId is not null
            && map.Prototypes[e.Name].Type == "electric-pole" && map.Prototypes[e.Name].SupplyArea is > 0)
            .OrderBy(e => e.Position.DistanceTo(original.Position)).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            double radius = map.Prototypes[pole.Name].SupplyArea!.Value;
            var coverage = new WorldBox(new(pole.Position.X - radius, pole.Position.Y - radius),
                new(pole.Position.X + radius, pole.Position.Y + radius));
            // This is a bounded search of observed terrain, not a proof that no other placement exists.
            foreach (var candidate in placement.FindCandidates(field, machineItem, pole.Position, requireBuildReach: false)
                .Where(p => coverage.Overlaps(geometry.CollisionBox.Rotate(p.Direction).Translate(p.Position))))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var projected = Project(map, entityId, candidate);
                var supplies = new MultiFluidSupplyPlanner().Find(projected, stock, pipeItem, PlannedId, fluids, cancellationToken);
                if (supplies is null) continue;
                var remaining = supplies.SelectMany(s => s.Supply.Route.Pipes).ToArray();
                var approach = placement.FindApproach(field, machineItem, candidate, remaining);
                if (approach is not null) return new(candidate, pole.Id, approach, supplies);
            }
        }
        return null;
    }

    public static SpatialSnapshot Project(SpatialSnapshot map, string entityId, PlacementCandidate candidate)
    {
        var original = map.Entities.Single(e => e.Id == entityId);
        int rotation = (candidate.Direction - original.Direction + 16) % 16;
        MapPosition Move(MapPosition point)
        {
            var offset = ExtractionPlanner.Rotate(new(point.X - original.Position.X, point.Y - original.Position.Y), rotation);
            return new(candidate.Position.X + offset.X, candidate.Position.Y + offset.Y);
        }
        var proposed = original with
        {
            Id = PlannedId, Position = candidate.Position, Direction = candidate.Direction,
            Bounds = map.Prototypes[original.Name].CollisionBox.Rotate(candidate.Direction).Translate(candidate.Position),
            FluidConnections = original.FluidConnections?.Select(p => p with
            {
                Position = Move(p.Position), TargetPosition = Move(p.TargetPosition),
                TargetEntityId = null, TargetBoxIndex = null
            }).ToArray(),
            Power = null
        };
        return map with { Entities = [.. map.Entities.Where(e => e.Id != entityId).Select(e => e with
        {
            FluidConnections = e.FluidConnections?.Select(p => p.TargetEntityId == entityId
                ? p with { TargetEntityId = null, TargetBoxIndex = null } : p).ToArray()
        }), proposed] };
    }
}
