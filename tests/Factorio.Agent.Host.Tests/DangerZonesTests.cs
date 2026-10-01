using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class DangerZonesTests
{
    // Campaign 2026-10-01 (seed 20261002): the first death of the spiral and the recovery death 22.6 tiles from it.
    private static readonly NativeDeathTransition Exploration = new(5, 3102356, 775, 1, new(130.98, -14.97));
    private static readonly NativeDeathTransition Recovery = new(6, 3104492, 1086, 1, new(108.83, -10.48));

    [Fact]
    public void ZoneIsActiveForTwentyGameMinutesOnItsOwnSurfaceOnly()
    {
        var zones = DangerZones.Empty("world").Record(Exploration);
        Assert.Equal([Exploration], zones.Active(1, Exploration.DeathTick));
        Assert.Equal([Exploration], zones.Active(1, Exploration.DeathTick + DangerZones.LifetimeTicks - 1));
        Assert.Empty(zones.Active(1, Exploration.DeathTick + DangerZones.LifetimeTicks));
        Assert.Empty(zones.Active(1, Exploration.DeathTick - 1));
        Assert.Empty(zones.Active(2, Exploration.DeathTick));
        Assert.Equal(Exploration.DeathTick + 72000, DangerZones.Expires(Exploration));
    }

    [Fact]
    public void RecordingIsIdempotentAndRefusesAConflictingDeath()
    {
        var zones = DangerZones.Empty("world").Record(Exploration);
        Assert.Same(zones, zones.Record(Exploration));
        Assert.Throws<InvalidDataException>(() => zones.Record(Exploration with { Position = new(0, 0) }));
        Assert.Throws<InvalidDataException>(() => zones.Record(Recovery with { Position = new(double.NaN, 0) }));
    }

    [Fact]
    public void MemoryForgetsExpiredZonesAndKeepsTheNewestWithinItsCapacity()
    {
        var zones = DangerZones.Empty("world").Record(Exploration).Record(Recovery);
        var later = new NativeDeathTransition(7, Exploration.DeathTick + DangerZones.LifetimeTicks, 1093, 1, new(175, -19));
        Assert.Equal([Recovery, later], zones.Record(later).Deaths);
        for (int index = 0; index < 40; index++)
            zones = zones.Record(new(10 + index, Recovery.DeathTick + 100 * (index + 1), 2000 + index, 1, new(index, 0)));
        Assert.Equal(DangerZones.Capacity, zones.Deaths.Count);
        Assert.Equal(Recovery.DeathTick + 4000, zones.Deaths[^1].DeathTick);
        Assert.DoesNotContain(Exploration, zones.Deaths);
    }

    [Fact]
    public void ZoneCoversItsRadiusAndTheStraightWaysThatEnterIt()
    {
        Assert.True(DangerZones.Covers(Exploration, Recovery.Position));
        Assert.False(DangerZones.Covers(Exploration, new(96, 0)));
        // From the base toward a corpse east of the zone, the straight way passes through it.
        Assert.True(DangerZones.Crosses(Exploration, new(0, 0), new(175, -19)));
        Assert.False(DangerZones.Crosses(Exploration, new(0, 0), new(90, 0)));
        Assert.False(DangerZones.Crosses(Exploration, new(0, -60), new(200, -60)));
    }

    [Theory]
    [InlineData(105, -5, "unit", 128, -10, true, "occupied")]       // A biter beside the corpse, seen from 28 tiles away.
    [InlineData(105, -5, "unit-spawner", 140, -20, true, "occupied")]
    [InlineData(105, -5, "turret", 128, -10, true, "clear")]        // Worms are left to routing, which keeps their range.
    [InlineData(105, -5, "unit", 60, 0, true, "clear")]             // Visible, but outside the zone.
    [InlineData(105, -5, "unit", 128, -10, false, "out-of-sight")]  // A truncated list proves nothing.
    [InlineData(80, 0, "unit", 60, 0, true, "out-of-sight")]        // Part of the zone lies beyond the 64-tile view.
    public void InspectionNeedsTheWholeZoneInACompleteNormalView(double x, double y, string type, double enemyX, double enemyY,
        bool complete, string verdict)
    {
        Assert.Equal(verdict, DangerZones.Inspect(Exploration, new(x, y), 64, complete, [(type, new MapPosition(enemyX, enemyY))]));
    }

    [Fact]
    public void ZonesBelongToOneWorld()
    {
        Assert.Throws<InvalidDataException>(() => DangerZones.Empty("world").Record(Exploration).Validate("other-world"));
    }
}
