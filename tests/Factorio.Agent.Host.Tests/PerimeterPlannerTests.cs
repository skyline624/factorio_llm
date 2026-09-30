using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class PerimeterPlannerTests
{
    private const double Range = 18; // gun-turret attack_parameters.range in base 2.0.77
    private static readonly WorldBox Factory = new(new(-6, -5), new(6, 5));

    private static PerimeterPlan Plan(SpatialSnapshot map, int layers = 2) =>
        new PerimeterPlanner().Plan(map, [Factory], "gun-turret", "stone-wall", Range, layers);

    [Fact]
    public void NeighbouringTurretsStayWithinNativeRangeSoEveryRingPointIsCoveredTwice()
    {
        var plan = Plan(FactoryMaps.Grass(40));
        var turrets = plan.Nests.Select(n => n.Turret.Position).ToArray();
        Assert.True(turrets.Length >= 4);
        for (int i = 0; i < turrets.Length; i++)
            Assert.True(turrets[i].DistanceTo(turrets[(i + 1) % turrets.Length]) <= Range, $"{turrets[i]} is out of range of its neighbour");
        Assert.True(plan.Spacing <= Range);
        Assert.Equal(0, plan.CoverageGaps);
        // One lost turret must not open a hole: every point of the turret line is within range of two turrets.
        foreach (var point in Border(Expand(plan.Ring, -1)))
            Assert.True(turrets.Count(t => t.DistanceTo(point) <= Range) >= 2, $"{point} is covered by fewer than two turrets");
        foreach (var turret in plan.Nests)
            Assert.True(Gap(TileBox(turret.Turret, 2), plan.Protected) >= 3, "Turrets must leave a walkway around the factory.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void WallsShieldOnlyTheOutwardSideInTheRequestedLayers(int layers)
    {
        var plan = Plan(FactoryMaps.Grass(40), layers);
        var outer = Expand(plan.Ring, layers);
        foreach (var nest in plan.Nests)
        {
            Assert.NotEmpty(nest.Walls);
            var turret = TileBox(nest.Turret, 2);
            foreach (var wall in nest.Walls)
            {
                var tile = TileBox(wall, 1);
                Assert.Equal("stone-wall", wall.Item);
                Assert.False(tile.Overlaps(plan.Ring), $"{wall} is inside the turret line");
                Assert.True(outer.Contains(tile), $"{wall} is beyond {layers} layers");
                Assert.True(Gap(tile, plan.Protected) > Gap(turret, plan.Protected), $"{wall} is not on the outward side");
                Assert.True(nest.Box.Contains(tile));
            }
            Assert.Equal(nest.Walls.Count, nest.Walls.Select(w => w.Position).Distinct().Count());
        }
        Assert.Equal(layers == 2, plan.Nests.SelectMany(n => n.Walls).Any(w => !Expand(plan.Ring, 1).Contains(TileBox(w, 1))));
    }

    [Fact]
    public void OpeningsKeepTheRingUnclosedAndTheActorCanLeaveAndReturn()
    {
        var map = FactoryMaps.Grass(40);
        var plan = Plan(map);
        Assert.True(plan.CanLeave);
        Assert.True(plan.CanEnter);
        for (int a = 0; a < plan.Nests.Count; a++)
            for (int b = a + 1; b < plan.Nests.Count; b++)
                Assert.True(Gap(plan.Nests[a].Box, plan.Nests[b].Box) >= 3, $"Nests {a} and {b} leave less than three tiles");
        // Independent check from beside the factory to beyond each side of the walls.
        var beside = new MapPosition(0, Factory.Min.Y - 1.5);
        var outer = Expand(plan.Ring, 4);
        foreach (var target in new MapPosition[] { new(0, outer.Min.Y), new(0, outer.Max.Y), new(outer.Min.X, 0), new(outer.Max.X, 0) })
        {
            Assert.Equal(RouteStatus.Found, new RoutePlanner().Find(Built(map, plan, beside), target, timeBudget: TimeSpan.FromSeconds(5)).Status);
            Assert.Equal(RouteStatus.Found, new RoutePlanner().Find(Built(map, plan, target), beside, timeBudget: TimeSpan.FromSeconds(5)).Status);
        }
    }

    [Fact]
    public void WaterAndBuildingsAreNeverBuiltOverAndTreesAreReportedForClearing()
    {
        var nominal = Plan(FactoryMaps.Grass(40));
        var occupied = nominal.Nests[1].Turret.Position;
        var shielded = nominal.Nests[0].Walls[0].Position;
        var machine = new SpatialEntity("machine", "assembling-machine-1", occupied, new(new(occupied.X - 1.2, occupied.Y - 1.2), new(occupied.X + 1.2, occupied.Y + 1.2)), 0, "agent");
        var tree = new SpatialEntity("tree-1", "tree", shielded, new(new(shielded.X - .4, shielded.Y - .4), new(shielded.X + .4, shielded.Y + .4)), 0, "neutral");
        static string Tile(int x, int y) => x >= 8 && y < -7 ? "water" : "grass";
        var map = FactoryMaps.Grass(40, [machine, tree], Tile);
        var plan = new PerimeterPlanner().Plan(map, [Factory], "gun-turret", "stone-wall", Range, 2);
        var planned = plan.Nests.SelectMany(n => n.Walls.Append(n.Turret))
            .Select(e => map.Prototypes[e.Item].CollisionBox.Translate(e.Position)).ToArray();
        Assert.DoesNotContain(planned, box => box.Overlaps(machine.Bounds));
        foreach (var box in planned)
            for (int x = (int)Math.Floor(box.Min.X); x < box.Max.X; x++)
                for (int y = (int)Math.Floor(box.Min.Y); y < box.Max.Y; y++)
                    Assert.Equal("grass", Tile(x, y));
        Assert.True(plan.SkippedWalls > 0);
        Assert.Contains(plan.Nests.SelectMany(n => n.Walls), w => w.Position == shielded);
        Assert.Contains(plan.Clearance, e => e.Id == "tree-1");
        Assert.True(plan.CanLeave && plan.CanEnter);
    }

    [Fact]
    public void ANestThatWouldCloseTheOnlyLandBridgeKeepsItsTurretButNotItsWalls()
    {
        // Land reaches one tile past the walls; beyond it only a four-tile bridge leads north.
        static string Tile(int x, int y) => x >= -14 && x < 14 && y >= -12 && y < 14 || x >= -2 && x < 2 && y < -12 ? "grass" : "water";
        var plan = Plan(FactoryMaps.Grass(40, tile: Tile));
        Assert.True(plan.CanLeave);
        Assert.True(plan.CanEnter);
        var bridge = new WorldBox(new(-2, -12), new(2, -10));
        Assert.DoesNotContain(plan.Nests.SelectMany(n => n.Walls), w => TileBox(w, 1).Overlaps(bridge));
        var north = plan.Nests.Single(n => n.Turret.Position.X == 0 && n.Turret.Position.Y < Factory.Min.Y);
        Assert.Empty(north.Walls);
        Assert.All(plan.Nests.Where(n => n != north), n => Assert.NotEmpty(n.Walls));
    }

    [Fact]
    public void RingBeyondTheObservedAreaIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => Plan(FactoryMaps.Grass(14)));
    }

    [Fact]
    public void ProtectionGrowsFromTheCoreByTheNearestIndustryThatStillFits()
    {
        var map = FactoryMaps.Grass(40);
        var near = new WorldBox(new(8, 0), new(10, 2));
        var far = new WorldBox(new(30, 30), new(32, 32));
        Assert.Equal(new[] { 1 }, PerimeterPlanner.Select(map, [Factory], [far, near], "gun-turret", 2, 3));
        Assert.Equal(new[] { 0 }, PerimeterPlanner.Select(map, [], [near, far], "gun-turret", 2, 3));
    }

    private static SpatialCollisionField Built(SpatialSnapshot map, PerimeterPlan plan, MapPosition actor) => new(map with
    {
        Actor = map.Actor with { Position = actor },
        Entities = [.. map.Entities, .. plan.Nests.SelectMany(n => n.Walls.Append(n.Turret)).Select((e, i) =>
            new SpatialEntity($"planned-{i}", e.Item, e.Position, map.Prototypes[e.Item].CollisionBox.Translate(e.Position), 0, "planned"))]
    });

    private static IEnumerable<MapPosition> Border(WorldBox box)
    {
        for (double x = box.Min.X; x <= box.Max.X; x += .5) { yield return new(x, box.Min.Y); yield return new(x, box.Max.Y); }
        for (double y = box.Min.Y; y <= box.Max.Y; y += .5) { yield return new(box.Min.X, y); yield return new(box.Max.X, y); }
    }

    private static WorldBox Expand(WorldBox box, double by) => new(new(box.Min.X - by, box.Min.Y - by), new(box.Max.X + by, box.Max.Y + by));
    private static WorldBox TileBox(PlannedEntity e, int size) =>
        new(new(e.Position.X - size / 2.0, e.Position.Y - size / 2.0), new(e.Position.X + size / 2.0, e.Position.Y + size / 2.0));
    private static double Gap(WorldBox a, WorldBox b) =>
        Math.Max(Math.Max(a.Min.X - b.Max.X, b.Min.X - a.Max.X), Math.Max(a.Min.Y - b.Max.Y, b.Min.Y - a.Max.Y));
}
