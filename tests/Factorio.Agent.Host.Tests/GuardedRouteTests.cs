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
    public void NeighbouringStartsShareTheSameDetour()
    {
        // Seed 987654321 on 2026-10-08: a grid moving with the actor sent it south-east from one point and five tiles away from
        // the next, and it walked between them for hours.
        (MapPosition, double)[] cliffs = [.. Enumerable.Range(0, 30).Select(i => (new MapPosition(300, 320 + 4 * i), 4.0))];
        MapPosition home = new(-32, -50);
        var detours = new[] { new MapPosition(312, 386), new(313, 387), new(314, 385), new(311.5, 386.5) }
            .Select(from => GuardedRoute.Waypoint(from, home, cliffs)).Distinct().ToArray();
        Assert.Single(detours);
    }

    [Fact]
    public void ALineOfCliffSegmentsIsWalkedAroundAtItsSeenEnd()
    {
        // Seed 987654321 on 2026-10-08: a cliff line west of the actor stood between it and the base and the steps paced along it.
        (MapPosition, double)[] cliffs = [.. Enumerable.Range(0, 30).Select(i => (new MapPosition(300, 320 + 4 * i), 4.0))];
        var from = new MapPosition(312, 386);
        var waypoint = GuardedRoute.Waypoint(from, new(-32, -50), cliffs)!;
        Assert.False(GuardedRoute.Crosses(from, waypoint, cliffs), $"The way to {waypoint} crosses the cliff line.");
        // The seen line ends at y 436 to the south and 320 to the north: the detour passes one of its ends.
        Assert.True(waypoint.Y < 320 || waypoint.Y > 436, $"The detour point {waypoint} does not round the line.");
    }

    [Fact]
    public void GuardsHoldingTheStartOrTheDestinationDoNotDivertTheTrip()
    {
        Assert.Equal(To, GuardedRoute.Waypoint(From, To, [(To, 48), (From, 48)]));
    }
}
