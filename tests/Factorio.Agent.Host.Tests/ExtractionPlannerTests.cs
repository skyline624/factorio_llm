using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ExtractionPlannerTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 4)]
    public void OutputConnectionIsDerivedFromNativeGeometryAndTranslatedReceiver(double x, double y)
    {
        var (map, catalog) = Setup(x, y);
        var plans = new ExtractionPlanner().Find(new(map), "drill-item", "ore", catalog, [map.Entities[0]]);
        Assert.NotEmpty(plans);
        Assert.All(plans, p =>
        {
            Assert.True(ExtractionPlanner.DropTile(p.OutputPosition).Overlaps(map.Entities[0].Bounds));
            Assert.Contains("deposit", p.ResourceIds);
            Assert.True(new SpatialCollisionField(map).PlacementClear(map.Prototypes["drill"], p.Drill.Position, p.Drill.Direction));
        });
        Assert.Contains(plans, p => p.Drill.Position == new MapPosition(x, y + 2) && p.Drill.Direction == 0);
    }

    [Fact]
    public void FractionalNativeVectorReachesTheQuantizedReceiverEdge()
    {
        var (map, catalog) = Setup(0, 0);
        var receiver = map.Entities[0] with { Bounds = new(new(-0.69921875, -0.69921875), new(0.69921875, 0.69921875)) };
        map = map with
        {
            Entities = [receiver, map.Entities[1]],
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["drill"] = map.Prototypes["drill"] with { MiningOutput = new(-0.5, -1.3), MiningRadius = 0.99 } }
        };
        var plans = new ExtractionPlanner().Find(new(map), "drill-item", "ore", catalog, [receiver]);
        Assert.Contains(plans, p => p.Drill.Position == new MapPosition(0, 2) && p.OutputPosition == new MapPosition(-0.5, 0.703125));
    }

    [Fact]
    public void MixedDepositIsRejectedRatherThanContaminatingTheFurnace()
    {
        var (map, catalog) = Setup(0, 0);
        var contamination = map.Entities[1] with
        {
            Id = "contamination",
            Name = "other-resource",
            Position = new(-0.5, 2.5),
            Bounds = new(new(-0.9, 2.1), new(-0.1, 2.9))
        };
        map = map with
        {
            Entities = [.. map.Entities, contamination],
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["other-resource"] = map.Prototypes["resource"] with { Name = "other-resource" } }
        };
        Assert.Empty(new ExtractionPlanner().Find(new(map), "drill-item", "ore", catalog, [map.Entities[0]]));
    }

    [Fact]
    public void EmptyPatchCannotBeTreatedAsAnExtractionConnection()
    {
        var (map, catalog) = Setup(0, 0);
        map = map with { Entities = [map.Entities[0]] };
        Assert.Empty(new ExtractionPlanner().Find(new(map), "drill-item", "ore", catalog, [map.Entities[0]]));
    }

    [Fact]
    public void CardinalRotationUsesNativeOutputVector()
    {
        Assert.Equal(new MapPosition(1.85, 0), ExtractionPlanner.Rotate(new(0, -1.85), 4));
        Assert.Equal(new MapPosition(0, 1.85), ExtractionPlanner.Rotate(new(0, -1.85), 8));
        Assert.Equal(new MapPosition(-1.85, 0), ExtractionPlanner.Rotate(new(0, -1.85), 12));
    }

    internal static (SpatialSnapshot, ProductionCatalog) Setup(double x, double y)
    {
        var scope = new ActorScope("world", "session", "actor", 1, 1);
        var ground = new CollisionMask([], false, false, false);
        var solid = new CollisionMask(["object"], false, false, false);
        var machineBox = new WorldBox(new(-0.7, -0.7), new(0.7, 0.7));
        var receiver = new SpatialEntity("receiver", "furnace", new(x, y), machineBox.Translate(new(x, y)), 0, "agent");
        var resource = new SpatialEntity("deposit", "resource", new(x + 0.5, y + 2.5),
            new(new(x + 0.1, y + 2.1), new(x + 0.9, y + 2.9)), 0, "neutral", 100);
        var prototypes = new Dictionary<string, EntityGeometry>
        {
            ["character"] = new("character", "character", new(new(-0.2, -0.2), new(0.2, 0.2)), solid, 1, 1),
            ["furnace"] = new("furnace", "furnace", machineBox, solid, 2, 2),
            ["drill"] = new("drill", "mining-drill", machineBox, solid, 2, 2, 1.25, new(0, -1.85),
                ResourceCategories: new Dictionary<string, bool> { ["solid"] = true }),
            ["resource"] = new("resource", "resource", new(new(-0.4, -0.4), new(0.4, 0.4)), ground, 1, 1,
                ResourceCategory: "solid")
        };
        var map = new SpatialSnapshot(scope, 1, 1, new(new(-12, -12), new(13, 13)), new("actor", "character", new(10, 0), 10, 10, "ai"),
            prototypes, new Dictionary<string, CollisionMask> { ["grass"] = ground },
            Enumerable.Range(-12, 25).Select(row => new TileRun(-12, row, 25, "grass")).ToArray(), [receiver, resource],
            new Dictionary<string, PlaceableItem> { ["drill-item"] = new("drill", 50) }, new(true, true, "current-character-local-area", 12));
        var catalog = new ProductionCatalog(scope, 1, [], new Dictionary<string, NativeItem>(),
            new Dictionary<string, NativeMaterial[]> { ["resource"] = [new("ore", "item", 1)], ["other-resource"] = [new("different-ore", "item", 1)] },
            new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
        return (map, catalog);
    }
}
