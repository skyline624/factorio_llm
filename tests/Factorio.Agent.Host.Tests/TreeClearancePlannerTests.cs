using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class TreeClearancePlannerTests
{
    [Fact]
    public void ClearanceSelectsTheReachableNeutralTreeTowardTheDestination()
    {
        var (map, catalog) = World();
        Assert.Equal("near-tree", new TreeClearancePlanner().Select(map, catalog, new(-20, 0))?.Id);
    }

    [Fact]
    public void ClearanceCannotDemolishFactoryBuildingsOrTreesOutsideMiningReach()
    {
        var (map, catalog) = World();
        map = map with { Entities = map.Entities.Where(e => e.Id != "near-tree").ToArray() };
        Assert.Null(new TreeClearancePlanner().Select(map, catalog));
    }

    private static (SpatialSnapshot, ProductionCatalog) World()
    {
        var map = SpatialPlannerTests.Map([]);
        var tree = new EntityGeometry("tree", "tree", new(new(-.4, -.4), new(.4, .4)), map.Prototypes[map.Actor.Name].Mask, 1, 1);
        var machine = tree with { Name = "machine", Type = "furnace" };
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { ["tree"] = tree, ["machine"] = machine },
            Entities = [new("near-tree", "tree", new(-1, 0), tree.CollisionBox.Translate(new(-1, 0)), 0, "neutral"),
                new("far-tree", "tree", new(8, 0), tree.CollisionBox.Translate(new(8, 0)), 0, "neutral"),
                new("factory", "machine", new(.5, 0), machine.CollisionBox.Translate(new(.5, 0)), 0, "agent"),
                new("owned-tree", "tree", new(0, -1), tree.CollisionBox.Translate(new(0, -1)), 0, "agent")] };
        var catalog = new ProductionCatalog(map.Scope, 1, [], new Dictionary<string, NativeItem>(),
            new Dictionary<string, NativeMaterial[]> { ["tree"] = [new("wood", "item", 4)] },
            new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
        return (map, catalog);
    }
}
