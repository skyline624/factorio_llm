namespace Factorio.Agent.Core;

public sealed record FluidRelaySite(PlacementCandidate Outlet, PipeRoutePlan Route);

/// <summary>Extends a native fluid source toward a destination through one bounded, locally observed pipe section.</summary>
public sealed class FluidRelayPlanner
{
    public const string PlannedId = "planned:fluid-relay";
    public const int Step = 24;
    public const int MaximumPipes = 40;

    public FluidRelaySite? Find(SpatialSnapshot map, string pipeItem, string sourceId, string fluid, MapPosition destination,
        CancellationToken cancellationToken = default)
    {
        var source = map.Entities.Single(e => e.Id == sourceId);
        var geometry = map.Prototypes[map.Items[pipeItem].EntityName];
        if (geometry.Type != "pipe" || geometry.TileWidth != 1 || geometry.TileHeight != 1 || geometry.FluidBoxes?.Count != 1)
            throw new InvalidDataException("Fluid relays require native ordinary one-tile pipe geometry.");
        double distance = source.Position.DistanceTo(destination);
        if (!double.IsFinite(distance) || distance <= Step) return null;
        var preferred = new MapPosition(source.Position.X + (destination.X - source.Position.X) * Step / distance,
            source.Position.Y + (destination.Y - source.Position.Y) * Step / distance);
        var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });
        foreach (var outlet in new PlacementPlanner().FindCandidates(field, pipeItem, preferred, requireBuildReach: false)
            .Where(p => p.Direction == 0 && p.Position.DistanceTo(source.Position) <= 32
                && p.Position.DistanceTo(destination) <= distance - 8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entity = new SpatialEntity(PlannedId, geometry.Name, outlet.Position,
                geometry.CollisionBox.Translate(outlet.Position), 0, source.Force,
                FluidConnections: FluidCellPlanner.Ports(geometry, outlet).Select(p => p with { Filter = fluid }).ToArray());
            // The outlet is built first; it must not join any existing network before its selected route is laid.
            if (entity.FluidConnections!.Any(p => map.Entities.Any(e => (e.FluidConnections ?? []).Any(other =>
                p.Position == other.TargetPosition && p.TargetPosition == other.Position)))) continue;
            var projected = map with { Entities = [.. field.Map.Entities, entity] };
            var route = new PipeRoutePlanner().Find(projected, pipeItem, sourceId, PlannedId, fluid, cancellationToken: cancellationToken);
            if (route.Status != PipeRouteStatus.Found || route.Pipes.Count > MaximumPipes) continue;
            var after = FluidCellPlanner.WithPipes(projected, pipeItem, route.Pipes, source.Force, "relay", reserveTiles: true);
            after = after with { Entities = after.Entities.Select(e => e.Id == PlannedId ? e with
                { Bounds = new(new(outlet.Position.X - .5, outlet.Position.Y - .5), new(outlet.Position.X + .5, outlet.Position.Y + .5)) } : e).ToArray() };
            if (new PlacementPlanner().FindApproach(field, pipeItem, outlet, route.Pipes, after) is null) continue;
            return new(outlet, route);
        }
        return null;
    }
}
