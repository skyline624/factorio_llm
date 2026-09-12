using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FluidConnectionPlannerTests
{
    [Theory]
    [InlineData(0, 0, 0, -1)]
    [InlineData(4, 1, 2, 1)]
    [InlineData(8, 0, 0, 1)]
    [InlineData(12, -1, -2, -1)]
    public void JoinsOppositeNativePortsAtAdjacentTiles(int direction, double x, double expectedX, double expectedY)
    {
        var (map, source) = Map();
        var sourcePlacement = new PlacementCandidate(new(x, x), direction, 0);
        var plans = new FluidConnectionPlanner().Find(new(map), source, sourcePlacement, "sink-item", "water");
        Assert.Contains(plans, p => p.Placement.Position == new MapPosition(expectedX, expectedY)
            && p.Placement.Direction == direction);
    }

    [Theory]
    [InlineData("steam", "input", "default")]
    [InlineData("water", "output", "default")]
    [InlineData("water", "input", "incompatible")]
    public void RejectsWrongFluidFlowOrConnectionCategory(string filter, string flow, string category)
    {
        var (map, source) = Map(filter, flow, category);
        Assert.Empty(new FluidConnectionPlanner().Find(new(map), source, new(new(0, 0), 0, 0), "sink-item", "water"));
    }

    [Fact]
    public void OccupiedTargetIsNotAConnectionPlan()
    {
        var (map, source) = Map();
        map = map with { Entities = [new("blocking", "wall", new(0, -1), new(new(-0.5, -1.5), new(0.5, -0.5)), 0, "agent")] };
        Assert.Empty(new FluidConnectionPlanner().Find(new(map), source, new(new(0, 0), 0, 0), "sink-item", "water"));
    }

    private static (SpatialSnapshot, EntityGeometry) Map(string filter = "water", string flow = "input", string category = "default")
    {
        SpatialSnapshot map = SpatialPlannerTests.Map([]);
        MapPosition[] center = [new(0, 0), new(0, 0), new(0, 0), new(0, 0)];
        var source = map.Prototypes["chest"] with
        {
            Name = "source",
            TileWidth = 2,
            TileHeight = 2,
            FluidBoxes = [new(1, "output", [new(1, "normal", 0, "output", center, ["default"])], "water")]
        };
        var sink = source with { Name = "sink", FluidBoxes = [new(1, "input", [new(1, "normal", 8, flow, center, [category])], filter)] };
        map = map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { ["source"] = source, ["sink"] = sink },
            Items = new Dictionary<string, PlaceableItem> { ["sink-item"] = new("sink", 50) }
        };
        return (map, source);
    }
}
