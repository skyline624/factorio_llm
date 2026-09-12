using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FluidBufferPlannerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyExtendsPipesWithReciprocalNativeConnectionToTheOutput(bool reciprocal)
    {
        var map = PipeRoutePlannerTests.Map();
        var source = map.Entities[0] with
        {
            FluidConnections = [new(1, 1, new(-.5, .5), new(.5, .5), "existing", 1, "normal", "output", "oil")]
        };
        var pipe = new SpatialEntity("existing", "pipe", new(.5, .5), new(new(.2, .2), new(.8, .8)), 0, "agent",
            FluidConnections: [new(1, 1, new(.5, .5), new(-.5, .5), reciprocal ? "source" : null, 1, "normal", "input-output"),
                new(1, 2, new(.5, .5), new(1.5, .5), Type: "normal", FlowDirection: "input-output")]);
        map = map with { Entities = [source, pipe] };
        var extension = new FluidBufferPlanner().Next(map, "pipe", "source", "oil");
        if (!reciprocal) Assert.Null(extension);
        else
        {
            Assert.NotNull(extension);
            Assert.Equal("existing", extension.Outlet.EntityId);
            Assert.Equal(new MapPosition(1.5, .5), extension.Position);
        }
    }

    [Fact]
    public void ExtendsTheActualOutputPortWithoutInventingCoordinates()
    {
        var map = PipeRoutePlannerTests.Map();
        var extension = new FluidBufferPlanner().Next(map, "pipe", "source", "oil");
        Assert.NotNull(extension);
        Assert.Equal(new MapPosition(.5, .5), extension.Position);
        Assert.Equal("source", extension.Outlet.EntityId);
    }

    [Fact]
    public void RefusesToJoinAnUnrelatedFluidPort()
    {
        var map = PipeRoutePlannerTests.Map();
        var other = map.Entities.Single(e => e.Id == "target") with
        {
            FluidConnections = [new(1, 1, new(.5, 1.5), new(.5, .5), Type: "normal", FlowDirection: "input-output", Filter: "water")]
        };
        map = map with { Entities = [map.Entities[0], other] };
        Assert.Null(new FluidBufferPlanner().Next(map, "pipe", "source", "oil"));
    }
}
