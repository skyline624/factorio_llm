using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FluidSupplyPlannerTests
{
    [Theory]
    [InlineData(50, true)]
    [InlineData(0, false)]
    public void RequiresRealStockInTheSelectedSourceBox(double amount, bool expected)
    {
        var map = PipeRoutePlannerTests.Map();
        var stock = new FactorySnapshot("s", map.Scope, 10, 100, Protocol.ToElement(new { }),
            [new("fluid", "fluid", "source", "buffer", Protocol.ToElement(new { aggregateSafe = true,
                contents = new Dictionary<string, double> { ["oil"] = amount }, sourceBoxes = new[] { new { entityId = "source", index = 1 } } }))]);
        var route = new FluidSupplyPlanner().Find(map, stock, "pipe", "target", "oil");
        Assert.Equal(expected, route is not null);
        if (expected) Assert.Equal("source", route!.SourceId);
    }
}
