namespace Factorio.Agent.Core;

public sealed record FluidBufferExtension(FluidEndpoint Outlet, MapPosition Position);

public sealed class FluidBufferPlanner
{
    public FluidBufferExtension? Next(SpatialSnapshot map, string pipeItem, string machineId, string fluid)
    {
        var pipe = map.Prototypes[map.Items[pipeItem].EntityName];
        if (pipe.Type != "pipe" || pipe.TileWidth != 1 || pipe.TileHeight != 1)
            throw new InvalidDataException("Buffer extension requires ordinary one-tile pipe geometry.");
        var connected = ConnectedPipeIds(map, machineId, fluid);
        var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });
        var candidates = new List<FluidBufferExtension>();
        foreach (var entity in map.Entities.Where(e => e.Id == machineId || connected.Contains(e.Id)))
            foreach (var port in entity.FluidConnections ?? [])
                if (port.Type == "normal" && port.TargetEntityId is null && (port.Filter is null || port.Filter == fluid)
                    && port.FlowDirection is "output" or "input-output")
                {
                    var outlet = Endpoint(entity, port);
                    if (field.PlacementClear(pipe, port.TargetPosition, 0)
                        && PipeRoutePlanner.ConnectionsSafe(map, port.TargetPosition, outlet, outlet, connected))
                        candidates.Add(new(outlet, port.TargetPosition));
                }
        return candidates.OrderBy(c => c.Position.DistanceTo(map.Actor.Position)).ThenBy(c => c.Position.Y)
            .ThenBy(c => c.Position.X).FirstOrDefault();
    }

    public static IReadOnlySet<string> ConnectedPipeIds(SpatialSnapshot map, string machineId, string fluid)
    {
        var machine = map.Entities.Single(e => e.Id == machineId);
        var outlets = (machine.FluidConnections ?? []).Where(p => p.Type == "normal"
            && p.FlowDirection is "output" or "input-output" && (p.Filter is null || p.Filter == fluid))
            .Select(p => Endpoint(machine, p)).ToArray();
        return map.Entities.Where(e => e.Force == machine.Force && map.Prototypes[e.Name].Type == "pipe"
            && (e.FluidConnections ?? []).Any(p => outlets.Any(o => FluidNetwork.IsConnected(map, o, Endpoint(e, p), fluid))))
            .Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
    }

    private static FluidEndpoint Endpoint(SpatialEntity entity, ObservedFluidConnection port) =>
        new(entity.Id, port.BoxIndex, port.PortIndex, port.Position, port.TargetPosition);
}
