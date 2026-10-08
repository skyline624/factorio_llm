using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class GuardedRouteTests
{
    private static readonly MapPosition From = new(-100, 220), To = new(300, 175);

    [Fact]
    public void AClearWayGoesStraightToTheDestination() =>
        Assert.Equal(To, GuardedRoute.Waypoint(From, To, [(new(100, 0), 30)]));

    [Fact]
    public void AWallOfRememberedNestsIsWalkedAroundNotThrough()
    {
        // Seed 20261106 on 2026-10-08: worms near (17, 180) and spawners near (28, 250) walled off the way to the oil field.
        (MapPosition, double)[] wall = [(new(17, 180), 38), (new(28, 250), 48), (new(26, 262), 48)];
        var waypoint = GuardedRoute.Waypoint(From, To, wall)!;
        Assert.NotEqual(To, waypoint);
        Assert.False(GuardedRoute.Crosses(From, waypoint, wall), $"The way to {waypoint} crosses a guard.");
        Assert.All(wall, g => Assert.True(waypoint.DistanceTo(g.Item1) > g.Item2));
    }

    [Fact]
    public void GuardsHoldingTheStartOrTheDestinationDoNotDivertTheTrip()
    {
        Assert.Equal(To, GuardedRoute.Waypoint(From, To, [(To, 48), (From, 48)]));
    }
}
