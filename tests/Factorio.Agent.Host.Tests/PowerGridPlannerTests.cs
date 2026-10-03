using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class PowerGridPlannerTests
{
    [Fact]
    public void CarriedUsablePolesTakePriorityOverManufacturingLongerOnes()
    {
        var map = Map();
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["long"] = map.Prototypes["pole"] with { Name = "long", MaxWireDistance = 32 } },
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["long-item"] = new("long", 50) } };
        string chosen = PowerGridPlanner.ChoosePole(map, ["pole-item", "long-item"], new Dictionary<string, long> { ["pole-item"] = 49 });
        Assert.Equal("pole-item", chosen);
    }

    [Fact]
    public void ExistingCoverageRequiresNoAdditionalPole()
    {
        var map = Map();
        var result = new PowerGridPlanner().Next(map, "pole-item", new(new(1, 0), new(2, 1)), new HashSet<string> { "source" });
        Assert.Equal(PowerGridSearchStatus.Connected, result.Status);
        Assert.Null(result.Pole);
        Assert.Equal("source", result.SourceId);
    }

    [Fact]
    public void LongerConnectionReturnsOnlyTheFirstNativeWireLink()
    {
        var map = Map();
        var target = new WorldBox(new(19, 0), new(22, 3));
        var result = new PowerGridPlanner().Next(map, "pole-item", target, new HashSet<string> { "source" });
        Assert.Equal(PowerGridSearchStatus.Extension, result.Status);
        Assert.NotNull(result.Pole);
        Assert.InRange(result.Pole.Position.DistanceTo(new(.5, .5)), .1, 7.5);
        Assert.False(target.Overlaps(map.Prototypes["pole"].CollisionBox.Translate(result.Pole.Position)));
    }

    [Fact]
    public void FirstLinkUsesTheShorterWireRangeOfBothPoleTypes()
    {
        var map = Map();
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["short"] = map.Prototypes["pole"] with { Name = "short", MaxWireDistance = 2 } },
            Entities = [map.Entities.Single() with { Name = "short" }] };
        var result = new PowerGridPlanner().Next(map, "pole-item", new(new(19, 0), new(22, 3)), new HashSet<string> { "source" });
        Assert.Equal(PowerGridSearchStatus.Extension, result.Status);
        Assert.InRange(result.Pole!.Position.DistanceTo(new(.5, .5)), .1, 2);
    }

    [Fact]
    public void UnownedOrDisconnectedPolesCannotPromiseAPowerSource()
    {
        var map = Map();
        var target = new WorldBox(new(19, 0), new(22, 3));
        Assert.Equal(PowerGridSearchStatus.NoObservedPath,
            new PowerGridPlanner().Next(map, "pole-item", target, new HashSet<string>()).Status);
        map = map with { Entities = [map.Entities.Single() with { Power = new(0) }] };
        Assert.Equal(PowerGridSearchStatus.NoObservedPath,
            new PowerGridPlanner().Next(map, "pole-item", target, new HashSet<string> { "source" }).Status);
    }

    [Fact]
    public void ABlockedDirectRouteCanUseObservedLandAroundTheWater()
    {
        var map = WaterBarrier(10);
        var result = new PowerGridPlanner().Next(map, "pole-item", new(new(19, 0), new(22, 3)), new HashSet<string> { "source" });
        Assert.Equal(PowerGridSearchStatus.Extension, result.Status);
        Assert.True(result.Pole!.Position.Y > .5);
        Assert.True(new SpatialCollisionField(map).PlacementClear(map.Prototypes["pole"], result.Pole.Position, 0));
    }

    [Fact]
    public void WaterWiderThanTheWireRangeDoesNotPromiseAConnection()
    {
        var result = new PowerGridPlanner().Next(WaterBarrier(24), "pole-item", new(new(19, 0), new(22, 3)),
            new HashSet<string> { "source" });
        Assert.Equal(PowerGridSearchStatus.NoObservedPath, result.Status);
        Assert.Null(result.Pole);
    }

    private static SpatialSnapshot WaterBarrier(int top)
    {
        var map = Map();
        return map with { TilePrototypes = new Dictionary<string, CollisionMask>(map.TilePrototypes)
            { ["water"] = new(["object"], false, false, false) },
            Rows = Enumerable.Range(-24, 49).SelectMany(y => y <= top
                ? new TileRun[] { new(-24, y, 28, "grass"), new(4, y, 11, "water"), new(15, y, 10, "grass") }
                : [new(-24, y, 49, "grass")]).ToArray() };
    }

    [Fact]
    public void ARemoteTargetUsesOnlyAnObservedExtension()
    {
        var map = Map();
        var result = new PowerGridPlanner().Next(map, "pole-item", new(new(100, 0), new(103, 3)), new HashSet<string> { "source" });
        Assert.Equal(PowerGridSearchStatus.Extension, result.Status);
        Assert.True(map.Bounds.Contains(result.Pole!.Position));
    }

    [Fact]
    public void APoleInsideAReservedBandIsJoinedByWireWithoutOccupyingTheBand()
    {
        var map = ReservedBand();
        var owned = new HashSet<string> { "source" };
        var target = map.Entities.Single(e => e.Id == "target");
        Assert.Equal(PowerGridSearchStatus.NoObservedPath,
            new PowerGridPlanner().Next(map, "pole-item", target.Bounds, owned).Status);

        var result = new PowerGridPlanner().NextToPole(map, "pole-item", target.Name, target.Position, owned);
        Assert.Equal(PowerGridSearchStatus.Extension, result.Status);
        Assert.True(new SpatialCollisionField(map).PlacementClear(map.Prototypes["pole"], result.Pole!.Position, 0));
        Assert.False(map.Entities.Single(e => e.Id == "band").Bounds.Overlaps(
            map.Prototypes["pole"].CollisionBox.Translate(result.Pole.Position)));
        Assert.InRange(result.Pole.Position.DistanceTo(map.Entities.Single(e => e.Id == "source").Position), .1, 7.5);
    }

    [Fact]
    public void TargetPolesOwnShorterWireRangeStillConstrainsTheConnection()
    {
        var map = ReservedBand();
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["short"] = map.Prototypes["pole"] with { Name = "short", MaxWireDistance = 2 } } };
        var result = new PowerGridPlanner().NextToPole(map, "pole-item", "short", new(20.5, .5), new HashSet<string> { "source" });
        Assert.Equal(PowerGridSearchStatus.NoObservedPath, result.Status);
        Assert.Null(result.Pole);
    }

    [Theory]
    [InlineData("character")]
    [InlineData("unknown")]
    public void OnlyNativePolesCanUseTheWireTarget(string prototype)
    {
        Assert.Throws<InvalidDataException>(() => new PowerGridPlanner().NextToPole(Map(), "pole-item", prototype,
            new(20.5, .5), new HashSet<string> { "source" }));
    }

    private static SpatialSnapshot ReservedBand()
    {
        var map = Map();
        var geometry = map.Prototypes["pole"];
        return map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["reservation"] = geometry with { Name = "reservation", Type = "reservation" } },
            Entities = [.. map.Entities,
                new("target", "pole", new(20.5, .5), geometry.CollisionBox.Translate(new(20.5, .5)), 0, "agent", Power: new(0, 8)),
                new("band", "reservation", new(19, 1.5), new(new(15, -5), new(23, 8)), 0, "planned")] };
    }

    internal static SpatialSnapshot Map()
    {
        CollisionMask solid = new(["object"], false, false, false), empty = new([], false, false, false);
        var pole = new EntityGeometry("pole", "electric-pole", new(new(-.15, -.15), new(.15, .15)), solid, 1, 1,
            SupplyArea: 2.5, MaxWireDistance: 7.5);
        return new(new("w", "s", "a", 1, 1), 100, 1, new(new(-24, -24), new(25, 25)),
            new("actor", "character", new(-2, -2), 10, 10, "ai"),
            new Dictionary<string, EntityGeometry> { ["pole"] = pole,
                ["character"] = new("character", "character", new(new(-.2, -.2), new(.2, .2)), solid, 1, 1) },
            new Dictionary<string, CollisionMask> { ["grass"] = empty },
            Enumerable.Range(-24, 49).Select(y => new TileRun(-24, y, 49, "grass")).ToArray(),
            [new("source", "pole", new(.5, .5), pole.CollisionBox.Translate(new(.5, .5)), 0, "agent", Power: new(0, 7))],
            new Dictionary<string, PlaceableItem> { ["pole-item"] = new("pole", 50) }, new(true, true, "current-character-local-area", 24));
    }
}
