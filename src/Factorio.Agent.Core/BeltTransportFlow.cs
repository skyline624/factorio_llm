namespace Factorio.Agent.Core;

public sealed record BeltTransportMeasurement(string SnapshotId, long CollectedTick, BeltTransportReading Reading, long Delivered, bool Energized);

/// <summary>A native baseline and its connected boundaries, retained while other ingredient lines are installed.</summary>
public sealed record BeltTransportFlow(ActorScope Scope, MaterialEndpoint Target, string Item, BeltTransportBoundary Boundary,
    BeltTransportInstallation Installation, BeltTransportReading Baseline, long StartTick)
{
    public BeltTransportMeasurement Measure(SpatialSnapshot map, FactorySnapshot stock)
    {
        if (map.Scope != Scope || stock.Scope != Scope) throw new InvalidDataException("Actor scope changed during transport accounting.");
        Boundary.VerifyConnection(map, Target.EntityId, Installation);
        var reading = Boundary.Read(stock, Target, Item, Installation.BeltIds, [Installation.SourceInserterId, Installation.TargetInserterId]);
        bool energized = map.Entities.Single(e => e.Id == Installation.SourceInserterId).Power?.Energy > 0
            && map.Entities.Single(e => e.Id == Installation.TargetInserterId).Power?.Energy > 0;
        return new(stock.SnapshotId, stock.CollectedTick, reading, reading.DeliveredSince(Baseline), energized);
    }
}
