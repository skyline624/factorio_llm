using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class UndergroundBeltRoutePlannerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void CrossesAnObservedBarrierWithPaidPhysicalEndsAndStraightApproaches(int direction)
    {
        var map = Map();
        bool horizontal = direction is 4 or 12;
        MapPosition start = horizontal ? new(-3.5, .5) : new(.5, -3.5);
        MapPosition end = horizontal ? new(4.5, .5) : new(.5, 4.5);
        if (direction is 0 or 12) (start, end) = (end, start);
        var box = horizontal ? new WorldBox(new(-.1, -12), new(1.1, 13)) : new(new(-12, -.1), new(13, 1.1));
        map = map with { Entities = [new("barrier", "wall", new(.5, .5), box, 0, "own")] };
        Assert.Equal(BeltRouteStatus.NoRouteInSnapshot, new BeltRoutePlanner().Find(map, "belt", start, end).Status);
        var route = new UndergroundBeltRoutePlanner().Find(map, "belt", "underground", start, end);
        Assert.Equal(BeltRouteStatus.Found, route.Status);
        Assert.Equal(start, route.Belts[0].Position);
        Assert.Equal(end, route.Belts[^1].Position);
        var input = Assert.Single(route.Belts, p => p.UndergroundType == "input");
        int index = route.Belts.ToList().IndexOf(input);
        var output = route.Belts[index + 1];
        Assert.Equal("output", output.UndergroundType);
        Assert.Equal(direction, input.Direction);
        Assert.Equal(input.Direction, output.Direction);
        Assert.InRange(input.Position.DistanceTo(output.Position), 2, 5);
        Assert.True(route.Belts.Count < 9);
        var collision = new SpatialCollisionField(map);
        Assert.All(route.Belts, p => Assert.True(collision.PlacementClear(map.Prototypes[p.UndergroundType is null ? "belt" : "underground"], p.Position, p.Direction)));
        if (index > 0) Assert.Equal(input.Direction, route.Belts[index - 1].Direction);
        if (index + 2 < route.Belts.Count) Assert.Equal(output.Direction, Direction(output.Position, route.Belts[index + 2].Position));
    }

    [Fact]
    public void DoesNotTunnelAcrossUnsureSurveyCoverage()
    {
        var map = Map();
        const string unknown = "__unsurveyed_transport_tile__";
        map = map with
        {
            TilePrototypes = new Dictionary<string, CollisionMask>(map.TilePrototypes) { [unknown] = map.Prototypes["wall"].Mask },
            Rows = map.Rows.SelectMany(row => new[] { row with { Length = 12 }, new TileRun(0, row.Y, 1, unknown), new TileRun(1, row.Y, 12, row.Name) }).ToArray()
        };
        var route = new UndergroundBeltRoutePlanner().Find(map, "belt", "underground", new(-3.5, .5), new(4.5, .5));
        Assert.Equal(BeltRouteStatus.NoRouteInSnapshot, route.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void ConsecutiveNativePairsCanShareAnAdjacentSurfaceConnection(int direction)
    {
        var map = Map();
        bool horizontal = direction is 4 or 12;
        MapPosition start = new(.5,.5), end = horizontal ? new(11.5,.5) : new(.5,11.5);
        if (direction is 0 or 12) (start,end) = (end,start);
        var first = horizontal ? new WorldBox(new(1,0),new(5,1)) : new(new(0,1),new(1,5));
        var second = horizontal ? new WorldBox(new(7,0),new(11,1)) : new(new(0,7),new(1,11));
        map = map with
        {
            Bounds = horizontal ? new(new(0,0),new(12,1)) : new(new(0,0),new(1,12)),
            Rows = horizontal ? [new(0,0,12,"grass")] : Enumerable.Range(0,12).Select(y=>new TileRun(0,y,1,"grass")).ToArray(),
            Entities = [new("first-barrier","wall",horizontal ? new(3,.5) : new(.5,3),first,0,"own"),
                new("second-barrier","wall",horizontal ? new(9,.5) : new(.5,9),second,0,"own")]
        };
        Assert.Equal(BeltRouteStatus.NoRouteInSnapshot,new BeltRoutePlanner().Find(map,"belt",start,end).Status);
        var route = new UndergroundBeltRoutePlanner().Find(map,"belt","underground",start,end);
        Assert.Equal(BeltRouteStatus.Found,route.Status);
        Assert.Equal(4,route.Belts.Count);
        Assert.Equal(new[] {"input","output","input","output"},route.Belts.Select(p=>p.UndergroundType));
        Assert.All(route.Belts,p=>Assert.Equal(direction,p.Direction));
        Assert.Equal(1,route.Belts[1].Position.DistanceTo(route.Belts[2].Position));
        Assert.Equal(5,route.Belts[0].Position.DistanceTo(route.Belts[1].Position));
        Assert.Equal(5,route.Belts[2].Position.DistanceTo(route.Belts[3].Position));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("outside-frame")]
    public void AForeignPartnerCannotBeClaimedByANewTunnel(string? partnerId)
    {
        var map = Map();
        var geometry = map.Prototypes["underground"];
        // A narrow photographed corridor leaves only the one eastward crossing.
        map = map with
        {
            Bounds = new(new(-4, 0), new(5, 1)),
            Rows = [new(-4, 0, 9, "grass")],
            Entities = [new("barrier", "wall", new(.5, .5), new(new(-.1, 0), new(1.1, 1)), 0, "own"),
                new("foreign", "underground", new(4.5, .5), geometry.CollisionBox.Translate(new(4.5, .5)), 4, "own",
                    Underground: new("output", 1, 2, partnerId))]
        };
        Assert.Equal(BeltRouteStatus.NoRouteInSnapshot,
            new UndergroundBeltRoutePlanner().Find(map, "belt", "underground", new(-3.5, .5), new(2.5, .5)).Status);
    }

    [Fact]
    public void NativeDistanceIsRequiredRatherThanInvented()
    {
        var map = Map();
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["underground"] = map.Prototypes["underground"] with { MaxUndergroundDistance = null } } };
        Assert.Throws<InvalidDataException>(() => new UndergroundBeltRoutePlanner().Find(map, "belt", "underground", new(-3.5, .5), new(4.5, .5)));
    }

    [Fact]
    public void ACompletedPairBehindTheNewInputDoesNotCompeteForItsPartner()
    {
        var map = Map();
        var geometry = map.Prototypes["underground"];
        MapPosition oldInput = new(-6.5,.5), oldOutput = new(-1.5,.5);
        map = map with
        {
            Bounds = new(new(-7,0),new(6,1)),
            Rows = [new(-7,0,13,"grass")],
            Entities = [new("barrier","wall",new(3,.5),new(new(1,0),new(5,1)),0,"own"),
                new("old-input","underground",oldInput,geometry.CollisionBox.Translate(oldInput),4,"own",Underground:new("input",1,2,"old-output")),
                new("old-output","underground",oldOutput,geometry.CollisionBox.Translate(oldOutput),4,"own",Underground:new("output",1,2,"old-input"))]
        };
        var route = new UndergroundBeltRoutePlanner().Find(map,"belt","underground",new(.5,.5),new(5.5,.5));
        Assert.Equal(BeltRouteStatus.Found,route.Status);
        Assert.Equal(2,route.Belts.Count);
        Assert.Equal("input",route.Belts[0].UndergroundType);
        Assert.Equal("output",route.Belts[1].UndergroundType);
    }

    [Fact]
    public void KeepsCallerCancellationAndNodeBudgetDistinct()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new UndergroundBeltRoutePlanner().Find(Map(), "belt", "underground", new(-3.5, .5), new(4.5, .5), token: cancellation.Token));
        Assert.Equal(BeltRouteStatus.BudgetExceeded,
            new UndergroundBeltRoutePlanner().Find(Map(), "belt", "underground", new(-3.5, .5), new(4.5, .5), nodeBudget: 1).Status);
    }

    private static int Direction(MapPosition a, MapPosition b) => b.X > a.X ? 4 : b.X < a.X ? 12 : b.Y > a.Y ? 8 : 0;

    internal static SpatialSnapshot Map()
    {
        var map = BeltRoutePlannerTests.Map();
        return map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
                { ["underground"] = map.Prototypes["belt"] with { Name = "underground", Type = "underground-belt", MaxUndergroundDistance = 5 } },
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["underground"] = new("underground", 50) }
        };
    }
}
