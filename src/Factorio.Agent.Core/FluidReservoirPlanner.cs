namespace Factorio.Agent.Core;

public sealed record FluidReservoirSite(string SourceId, PlacementCandidate Tank, PipeRoutePlan Route, MapPosition? Approach = null);
public sealed record PlannedFluidReservoir(string Fluid, FluidReservoirSite Site);

/// <summary>Places native tanks and routes them without mixing co-products or closing the actor's exit.</summary>
public sealed class FluidReservoirPlanner
{
    public const string PlannedId = "planned:fluid-reservoir";

    public FluidReservoirSite? Find(SpatialSnapshot map, string tankItem, string pipeItem, string machineId, string fluid,
        CancellationToken cancellationToken = default)
        => Candidates(map, tankItem, pipeItem, machineId, fluid, cancellationToken).FirstOrDefault();

    /// <summary>All co-products must have isolated storage routes together before the first tank is committed.</summary>
    public IReadOnlyList<PlannedFluidReservoir>? FindAll(SpatialSnapshot map, string tankItem, string pipeItem, string machineId,
        IReadOnlyList<string> fluids, CancellationToken cancellationToken = default)
    {
        if (fluids.Count is < 1 or > 3 || fluids.Distinct(StringComparer.Ordinal).Count() != fluids.Count)
            throw new ArgumentException("Joint reservoirs require one to three distinct fluids.");
        int searches = 0;
        return Search(map, 0);

        IReadOnlyList<PlannedFluidReservoir>? Search(SpatialSnapshot current, int index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index == fluids.Count) return [];
            if (++searches > 96) return null;
            string fluid = fluids[index];
            foreach (var site in Candidates(current, tankItem, pipeItem, machineId, fluid, cancellationToken))
            {
                var proposed = new PlannedFluidReservoir(fluid, site);
                var after = Project(current, tankItem, pipeItem, proposed);
                var tail = Search(after, index + 1);
                if (tail is not null) return [proposed, .. tail];
            }
            return null;
        }
    }

    /// <summary>Future tank and pipes reserve both their collision and their fluid connections for other routes.</summary>
    public static SpatialSnapshot Project(SpatialSnapshot map, string tankItem, string pipeItem, PlannedFluidReservoir planned)
    {
        var site = planned.Site;
        string id = PlannedId + ":" + planned.Fluid;
        var geometry = map.Prototypes[map.Items[tankItem].EntityName];
        string force = map.Entities.Single(e => e.Id == site.SourceId).Force;
        var tank = new SpatialEntity(id, geometry.Name, site.Tank.Position,
            geometry.CollisionBox.Rotate(site.Tank.Direction).Translate(site.Tank.Position), site.Tank.Direction, force,
            FluidConnections: FluidCellPlanner.Ports(geometry, site.Tank).Select(p => p with { Filter = planned.Fluid }).ToArray());
        var projected = FluidCellPlanner.WithPipes(map with { Entities = [.. map.Entities, tank] }, pipeItem, site.Route.Pipes,
            force, "reservoir:" + planned.Fluid, reserveTiles: true);
        return projected with
        {
            Actor = map.Actor with { Position = site.Approach ?? map.Actor.Position },
            Entities = projected.Entities.Select(e => e.Id != site.SourceId ? e : e with
            {
                FluidConnections = (e.FluidConnections ?? []).Select(p => p.BoxIndex != site.Route.Source!.BoxIndex ? p
                    : p with { TargetEntityId = id }).ToArray()
            }).ToArray()
        };
    }

    private static IReadOnlyList<FluidReservoirSite> Candidates(SpatialSnapshot map, string tankItem, string pipeItem,
        string machineId, string fluid, CancellationToken cancellationToken)
    {
        var machine = map.Entities.Single(e => e.Id == machineId);
        var geometry = map.Prototypes[map.Items[tankItem].EntityName];
        if (geometry.Type != "storage-tank" || geometry.FluidBoxes?.Count != 1)
            throw new InvalidDataException("A reservoir requires native single-fluid tank geometry.");
        var sources = FluidBufferPlanner.ConnectedStorageIds(map, machineId, fluid).Append(machineId).ToHashSet(StringComparer.Ordinal);
        var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });
        var sites = new List<FluidReservoirSite>();
        var outlet = (machine.FluidConnections ?? []).First(p => p.Type == "normal" && p.Filter == fluid
            && p.FlowDirection is "output" or "input-output");
        foreach (var placement in new PlacementPlanner().FindCandidates(field, tankItem, outlet.TargetPosition, requireBuildReach: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tank = new SpatialEntity(PlannedId, geometry.Name, placement.Position,
                geometry.CollisionBox.Rotate(placement.Direction).Translate(placement.Position), placement.Direction, machine.Force,
                FluidConnections: FluidCellPlanner.Ports(geometry, placement));
            if (map.Entities.Any(e => map.Prototypes[e.Name].Type == "resource" && e.Bounds.Overlaps(tank.Bounds))) continue;
            // Keep all the machine's other outlets open and reject a tank that would directly join another fluid network.
            if (map.Entities.SelectMany(e => e.FluidConnections ?? []).Any(p => p.Filter != fluid && p.Filter is not null
                && p.FlowDirection is "output" or "input-output" && tank.Bounds.Contains(p.TargetPosition))) continue;
            if (tank.FluidConnections!.Any(p => map.Entities.Any(e => (e.FluidConnections ?? []).Any(other =>
                p.Position == other.TargetPosition && p.TargetPosition == other.Position
                && (!sources.Contains(e.Id) || other.Filter is not null && other.Filter != fluid))))) continue;
            var projected = map with { Entities = [.. field.Map.Entities, tank] };
            foreach (string source in sources.Order(StringComparer.Ordinal))
            {
                var route = new PipeRoutePlanner().Find(projected, pipeItem, source, PlannedId, fluid, cancellationToken: cancellationToken);
                if (route.Status != PipeRouteStatus.Found || route.Pipes.Count > 32) continue;
                var pipe = map.Prototypes[map.Items[pipeItem].EntityName];
                var after = new SpatialCollisionField(projected with { Entities = [.. projected.Entities,
                    .. route.Pipes.Select((at, i) => new SpatialEntity($"{PlannedId}:pipe:{i}", pipe.Name, at,
                        new(new(at.X - .5, at.Y - .5), new(at.X + .5, at.Y + .5)), 0, machine.Force))] });
                var approach = new PlacementPlanner().FindApproach(field, tankItem, placement, completedSite: after.Map);
                if (approach is null) continue;
                after = new(after.Map with { Actor = after.Map.Actor with { Position = approach } });
                if (!PlacementPlanner.CanEscape(after, tank.Bounds)
                    || new PlacementPlanner().FindInteractionApproach(after, tank) is null
                    || new PlacementPlanner().FindInteractionApproach(after, machine) is null) continue;
                var candidate = new FluidReservoirSite(source, placement, route, approach);
                var future = Project(map, tankItem, pipeItem, new(fluid, candidate));
                if (!OtherOutletsRemainOpen(future, pipeItem, machineId)) continue;
                sites.Add(candidate);
                break;
            }
            if (sites.Count == 12) break;
        }
        return sites.OrderBy(s => s.Route.Pipes.Count).ThenBy(s => s.Tank.Score).ToArray();
    }

    private static bool OtherOutletsRemainOpen(SpatialSnapshot map, string pipeItem, string machineId)
    {
        var machine = map.Entities.Single(e => e.Id == machineId);
        var pipe = map.Prototypes[map.Items[pipeItem].EntityName];
        var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });
        foreach (var port in (machine.FluidConnections ?? []).Where(p => p.Type == "normal" && p.TargetEntityId is null
            && p.FlowDirection is "output" or "input-output"))
        {
            var endpoint = new FluidEndpoint(machine.Id, port.BoxIndex, port.PortIndex, port.Position, port.TargetPosition);
            bool Safe(MapPosition at) => field.PlacementClear(pipe, at, 0)
                && PipeRoutePlanner.ConnectionsSafe(map, at, endpoint, endpoint, new HashSet<string>());
            if (!Safe(port.TargetPosition)) return false;
            // A first tile alone can be a dead end. The next tile must also leave the other outlet starts isolated.
            if (!new MapPosition[] { new(port.TargetPosition.X + 1, port.TargetPosition.Y), new(port.TargetPosition.X - 1, port.TargetPosition.Y),
                new(port.TargetPosition.X, port.TargetPosition.Y + 1), new(port.TargetPosition.X, port.TargetPosition.Y - 1) }.Any(at =>
                Safe(at) && !(machine.FluidConnections ?? []).Any(other => other.BoxIndex != port.BoxIndex && other.TargetEntityId is null
                    && other.Type == "normal" && other.FlowDirection is "output" or "input-output"
                    && Math.Abs(other.TargetPosition.X - at.X) + Math.Abs(other.TargetPosition.Y - at.Y) <= 1.01))) return false;
        }
        return true;
    }
}
