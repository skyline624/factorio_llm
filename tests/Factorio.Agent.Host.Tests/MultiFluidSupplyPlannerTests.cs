using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class MultiFluidSupplyPlannerTests
{
    [Theory]
    [InlineData(100, true)]
    [InlineData(0, false)]
    public void PlanningRequiresBothInputsBeforeReturningAnyConstruction(double water, bool expected)
    {
        var map = PipeRoutePlannerTests.Map();
        var target = map.Entities.Single(e => e.Id == "target");
        var source = new SpatialEntity("water", "wall", new(5.5, 5.5), new(new(5, 5), new(6, 6)), 0, "agent",
            FluidConnections: [new(1, 1, new(5.5, 5.5), new(5.5, 4.5), Type: "normal", FlowDirection: "output", Filter: "water")]);
        target = target with { FluidConnections = [.. target.FluidConnections!,
            new(2, 1, new(5.5, 1.5), new(5.5, 2.5), Type: "normal", FlowDirection: "input", Filter: "water")] };
        map = map with { Entities = [map.Entities.Single(e => e.Id == "source"), source, target] };
        FactoryRecord Fluid(string id, string name, double amount) => new(id, "fluid", id, "fluid", Protocol.ToElement(new
        {
            aggregateSafe = true, contents = new Dictionary<string, double> { [name] = amount },
            sourceBoxes = new[] { new { entityId = id, index = 1 } }
        }));
        var stock = new FactorySnapshot("s", map.Scope, 1, 100, Protocol.ToElement(new { }), [Fluid("source", "oil", 100), Fluid("water", "water", water)]);
        var plans = new MultiFluidSupplyPlanner().Find(map, stock, "pipe", "target", ["oil", "water"]);
        Assert.Equal(expected, plans is not null);
        if (plans is null) return;
        Assert.Equal(2, plans.Count);
        Assert.All(plans[0].Supply.Route.Pipes, a => Assert.All(plans[1].Supply.Route.Pipes,
            b => Assert.True(Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) > 1)));
    }
}
