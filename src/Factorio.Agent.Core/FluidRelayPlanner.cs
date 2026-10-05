namespace Factorio.Agent.Core;

public sealed record FluidRelaySite(PlacementCandidate Outlet, PipeRoutePlan Route, PlannedMachine? Pump = null);
/// <summary>Preparation only: a bounded set of observed trees, followed by a mandatory fresh native relay search.</summary>
public sealed record FluidRelayClearancePlan(FluidRelaySite Site, IReadOnlyList<SpatialEntity> Trees, IReadOnlyList<MapPosition> Footprints);

/// <summary>Extends a native fluid source toward a destination through one bounded, locally observed pipe section.</summary>
public sealed class FluidRelayPlanner
{
    public const string PlannedId = "planned:fluid-relay";
    public const int Step = 24;
    public const int MaximumPipes = 40;
    public const int MaximumClearanceTrees = 16;

    /// <summary>
    /// When a source cannot be extended on the current ground, test a route through mineable neutral trees. Only trees
    /// touching the proposed pipe footprints may be removed, and that limited projection must still prove route and access.
    /// This never authorizes construction: the controller clears natively, then repeats Find on a new observation.
    /// </summary>
    public FluidRelayClearancePlan? FindClearance(SpatialSnapshot map, string pipeItem, string sourceId, string fluid,
        MapPosition destination, ProductionCatalog catalog, CancellationToken cancellationToken = default,
        IReadOnlySet<int>? sourceBoxIndices = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (map.Scope != catalog.Scope) throw new InvalidDataException("Relay clearance requires the current actor scope.");
        var trees = map.Entities.Where(e => TreeClearancePlanner.Clearable(map, catalog, e)).ToArray();
        if (trees.Length == 0) return null;
        var treeIds = trees.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var projected = map with { Entities = map.Entities.Where(e => !treeIds.Contains(e.Id)).ToArray() };
        var proposal = Find(projected, pipeItem, sourceId, fluid, destination, cancellationToken, sourceBoxIndices);
        if (proposal is null) return null;
        var geometry = map.Prototypes[map.Items[pipeItem].EntityName];
        var positions = proposal.Route.Pipes.Append(proposal.Outlet.Position).Distinct().ToArray();
        var footprints = positions.Select(p => geometry.CollisionBox.Translate(p)).ToArray();
        var chosen = new List<SpatialEntity>();
        foreach (var tree in trees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (geometry.Mask.CollidesWith(map.Prototypes[tree.Name].Mask, tile: false)
                && footprints.Any(box => new OrientedCollisionBox(tree.Bounds, tree.BoundsOrientation).TouchesOrOverlaps(box))) chosen.Add(tree);
            if (chosen.Count > MaximumClearanceTrees) return null;
        }
        if (chosen.Count == 0) return null;
        var selected = chosen.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var limited = map with { Entities = map.Entities.Where(e => !selected.Contains(e.Id)).ToArray() };
        var site = Find(limited, pipeItem, sourceId, fluid, destination, cancellationToken, sourceBoxIndices);
        return site is null ? null : new(site, chosen.AsReadOnly(), positions.Where(p => chosen.Any(tree =>
            new OrientedCollisionBox(tree.Bounds, tree.BoundsOrientation).TouchesOrOverlaps(geometry.CollisionBox.Translate(p)))).ToArray());
    }

    public FluidRelaySite? Find(SpatialSnapshot map, string pipeItem, string sourceId, string fluid, MapPosition destination,
        CancellationToken cancellationToken = default, IReadOnlySet<int>? sourceBoxIndices = null)
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
        foreach (var outlet in new PlacementPlanner().FindCandidates(field, pipeItem, preferred, requireBuildReach: false,
            eligible: p => p.Direction == 0 && p.Position.DistanceTo(source.Position) <= 32
                && p.Position.DistanceTo(destination) <= distance - 8, cancellationToken: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entity = new SpatialEntity(PlannedId, geometry.Name, outlet.Position,
                geometry.CollisionBox.Translate(outlet.Position), 0, source.Force,
                FluidConnections: FluidCellPlanner.Ports(geometry, outlet).Select(p => p with { Filter = fluid }).ToArray());
            // The outlet is built first; it must not join any existing network before its selected route is laid.
            if (entity.FluidConnections!.Any(p => map.Entities.Any(e => (e.FluidConnections ?? []).Any(other =>
                p.Position == other.TargetPosition && p.TargetPosition == other.Position)))) continue;
            var projected = map with { Entities = [.. field.Map.Entities, entity] };
            var route = new PipeRoutePlanner().Find(projected, pipeItem, sourceId, PlannedId, fluid, cancellationToken: cancellationToken,
                sourceBoxIndices: sourceBoxIndices);
            if (route.Status != PipeRouteStatus.Found || route.Pipes.Count > MaximumPipes) continue;
            var after = FluidCellPlanner.WithPipes(projected, pipeItem, route.Pipes, source.Force, "relay", reserveTiles: true);
            after = after with { Entities = after.Entities.Select(e => e.Id == PlannedId ? e with
                { Bounds = new(new(outlet.Position.X - .5, outlet.Position.Y - .5), new(outlet.Position.X + .5, outlet.Position.Y + .5)) } : e).ToArray() };
            if (new PlacementPlanner().FindApproach(field, pipeItem, outlet, route.Pipes, after, cancellationToken) is null) continue;
            return new(outlet, route);
        }
        return null;
    }

    /// <summary>Starts a separate terrain intake near an observed source when its existing ports cannot supply a safe relay.</summary>
    public FluidRelaySite? FindOffshore(SpatialSnapshot map, string pumpItem, string pipeItem, MapPosition near,
        string fluid, MapPosition destination, CancellationToken cancellationToken = default)
    {
        double distance = near.DistanceTo(destination);
        if (!double.IsFinite(distance) || distance <= Step) return null;
        if (!map.Rows.Any(r => map.TileFluids?.GetValueOrDefault(r.Name) == fluid)) return null;
        var preferred = new MapPosition(near.X + (destination.X - near.X) * Step / distance,
            near.Y + (destination.Y - near.Y) * Step / distance);
        var geometry = map.Prototypes[map.Items[pipeItem].EntityName];
        var pump = map.Prototypes[map.Items[pumpItem].EntityName];
        if (geometry.Type != "pipe" || geometry.TileWidth != 1 || geometry.TileHeight != 1 || geometry.FluidBoxes?.Count != 1
            || pump.Type != "offshore-pump")
            throw new InvalidDataException("Terrain relays require native ordinary pipes and an offshore pump.");
        var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });
        string force = map.Entities.Single(e => e.Id == map.Actor.Id).Force;
        foreach (var outlet in new PlacementPlanner().FindCandidates(field, pipeItem, preferred, requireBuildReach: false,
            eligible: p => p.Direction == 0 && p.Position.DistanceTo(near) <= 32
                && p.Position.DistanceTo(destination) <= distance - 8, cancellationToken: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entity = new SpatialEntity(PlannedId, geometry.Name, outlet.Position, geometry.CollisionBox.Translate(outlet.Position),
                0, force, FluidConnections: FluidCellPlanner.Ports(geometry, outlet).Select(p => p with { Filter = fluid }).ToArray());
            if (entity.FluidConnections!.Any(p => map.Entities.Any(e => (e.FluidConnections ?? []).Any(other =>
                p.Position == other.TargetPosition && p.TargetPosition == other.Position)))) continue;
            var projected = map with { Entities = [.. field.Map.Entities, entity] };
            var supply = new OffshoreSupplyPlanner().Find(projected, pumpItem, pipeItem, PlannedId, fluid, cancellationToken);
            // The outlet already advances at least eight tiles from the known shore. A new intake can stand
            // beside it; requiring another eight tiles from that intake rejects short, valid native connections.
            if (supply is null || supply.Route.Pipes.Count > MaximumPipes) continue;
            var nativePump = new SpatialEntity(supply.Route.Source!.EntityId, pump.Name, supply.Pump.Position,
                pump.CollisionBox.Rotate(supply.Pump.Direction).Translate(supply.Pump.Position), supply.Pump.Direction, force,
                FluidConnections: FluidCellPlanner.Ports(pump, supply.Pump));
            var after = FluidCellPlanner.WithPipes(projected with { Entities = [.. projected.Entities, nativePump] },
                pipeItem, supply.Route.Pipes, force, "relay", reserveTiles: true);
            after = after with { Entities = after.Entities.Select(e => e.Id == PlannedId ? e with
                { Bounds = new(new(outlet.Position.X - .5, outlet.Position.Y - .5), new(outlet.Position.X + .5, outlet.Position.Y + .5)) } : e).ToArray() };
            if (new PlacementPlanner().FindApproach(field, pumpItem, supply.Pump, supply.Route.Pipes.Append(outlet.Position).ToArray(), after, cancellationToken) is null
                || new PlacementPlanner().FindApproach(field, pipeItem, outlet, supply.Route.Pipes, after, cancellationToken) is null) continue;
            return new(outlet, supply.Route, new("pump", pumpItem, supply.Pump));
        }
        return null;
    }
}
