namespace Factorio.Agent.Core;

public sealed record OffshoreSupplyPlan(PlacementCandidate Pump, PipeRoutePlan Route);

public sealed class OffshoreSupplyPlanner
{
    public static bool CanExtract(SpatialSnapshot map, string sourceId, string fluid)
    {
        var source = map.Entities.Single(e => e.Id == sourceId);
        var geometry = map.Prototypes[source.Name];
        if (geometry.Type != "offshore-pump" || geometry.FluidSourceOffset is null
            || source.FluidConnections?.Any(p => p.Type == "normal" && p.FlowDirection is "output" or "input-output"
                && (p.Filter is null || p.Filter == fluid)) != true) return false;
        var offset = ExtractionPlanner.Rotate(geometry.FluidSourceOffset, source.Direction);
        return new SpatialCollisionField(map).FluidAt(new(source.Position.X + offset.X, source.Position.Y + offset.Y)) == fluid;
    }

    public OffshoreSupplyPlan? Find(SpatialSnapshot map, string pumpItem, string pipeItem, string targetId, string fluid)
    {
        var geometry = map.Prototypes[map.Items[pumpItem].EntityName];
        if (geometry.FluidSourceOffset is null) throw new InvalidDataException("Missing native offshore fluid-source offset.");
        var target = map.Entities.Single(e => e.Id == targetId);
        var field = new SpatialCollisionField(map);
        foreach (var candidate in new PlacementPlanner().FindCandidates(field, pumpItem, target.Position, requireBuildReach: false))
        {
            var offset = ExtractionPlanner.Rotate(geometry.FluidSourceOffset, candidate.Direction);
            if (field.FluidAt(new(candidate.Position.X + offset.X, candidate.Position.Y + offset.Y)) != fluid) continue;
            var ports = new List<ObservedFluidConnection>();
            foreach (var box in geometry.FluidBoxes ?? [])
                foreach (var port in box.Connections.Where(p => p.Type == "normal" && p.FlowDirection is "output" or "input-output"))
                {
                    if (port.Positions.Count != 4) throw new InvalidDataException("Missing native offshore port rotations.");
                    var local = port.Positions[candidate.Direction / 4];
                    var position = new MapPosition(candidate.Position.X + local.X, candidate.Position.Y + local.Y);
                    var forward = ExtractionPlanner.Rotate(new(0, -1), (port.Direction + candidate.Direction) % 16);
                    ports.Add(new(box.Index, port.Index, position, new(position.X + forward.X, position.Y + forward.Y),
                        Type: port.Type, FlowDirection: port.FlowDirection, Filter: box.Filter));
                }
            var proposed = new SpatialEntity("planned:offshore-supply", geometry.Name, candidate.Position,
                geometry.CollisionBox.Rotate(candidate.Direction).Translate(candidate.Position), candidate.Direction, target.Force,
                FluidConnections: ports);
            var withPump = map with { Entities = [.. map.Entities, proposed] };
            var route = new PipeRoutePlanner().Find(withPump, pipeItem, proposed.Id, targetId, fluid);
            if (route.Status == PipeRouteStatus.Found) return new(candidate, route);
        }
        return null;
    }
}
