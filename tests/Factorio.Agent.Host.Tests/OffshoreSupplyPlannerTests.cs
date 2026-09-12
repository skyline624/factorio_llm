using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class OffshoreSupplyPlannerTests
{
    [Theory]
    [InlineData(true, 100, true)]
    [InlineData(false, 100, false)]
    [InlineData(true, 0, false)]
    public void NewPumpRequiresCompatibleRoutesForEveryInput(bool water, double gas, bool expected)
    {
        var map = SteamPowerPlannerTests.Map(water);
        map = map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            {
                ["pump"] = map.Prototypes["pump"] with { Type = "offshore-pump" },
                ["pipe"] = PipeRoutePlannerTests.Map().Prototypes["pipe"]
            },
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["pipe"] = new("pipe", 100) },
            Entities =
            [
                new("target", "load", new(3.5, 4.5), new(new(2, 4), new(5, 5)), 0, "own", FluidConnections:
                [new(17, 1, new(2.5, 4.5), new(2.5, 3.5), Type: "normal", FlowDirection: "input", Filter: "water"),
                 new(23, 1, new(4.5, 4.5), new(4.5, 3.5), Type: "normal", FlowDirection: "input", Filter: "gas")]),
                new("gas", "load", new(7.5, 5.5), new(new(7, 5), new(8, 6)), 0, "own", FluidConnections:
                [new(1, 1, new(7.5, 5.5), new(6.5, 5.5), Type: "normal", FlowDirection: "output", Filter: "gas")])
            ]
        };
        var stock = new FactorySnapshot("s", map.Scope, 1, 100, Protocol.ToElement(new { }),
            [new("gas", "fluid", "gas", "gas", Protocol.ToElement(new { aggregateSafe = true,
                contents = new Dictionary<string, double> { ["gas"] = gas }, sourceBoxes = new[] { new { entityId = "gas", index = 1 } } }))]);
        var plan = new OffshoreSupplyPlanner().FindJoint(map, stock, "pump", "pipe", "target", "water", ["water", "gas"]);
        Assert.Equal(expected, plan is not null);
        if (plan is null) return;
        Assert.Equal(2, plan.Supplies.Count);
        Assert.Equal(17, plan.Supplies.Single(s => s.Fluid == "water").Supply.Route.Target!.BoxIndex);
        Assert.Equal(23, plan.Supplies.Single(s => s.Fluid == "gas").Supply.Route.Target!.BoxIndex);
    }

    [Theory]
    [InlineData(true, "water", true)]
    [InlineData(false, "water", false)]
    [InlineData(true, "acid", false)]
    public void AnEmptyPumpNeedsBothNativeIntakeAndOutputEvidence(bool water, string fluid, bool expected)
    {
        var map = SteamPowerPlannerTests.Map(water);
        var pump = new SpatialEntity("existing", "pump", new(.5, .5), new(new(.35, .35), new(.65, .65)), 0, "own",
            FluidConnections: [new(1, 1, new(.5, .5), new(.5, 1.5), Type: "normal", FlowDirection: "output", Filter: "water")]);
        map = map with { Entities = [pump], Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["pump"] = map.Prototypes["pump"] with { Type = "offshore-pump" } } };
        Assert.Equal(expected, OffshoreSupplyPlanner.CanExtract(map, "existing", fluid));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void IndependentPumpRequiresObservedFluidAndACompatibleRoute(bool water, bool expected)
    {
        var map = SteamPowerPlannerTests.Map(water);
        var pipe = PipeRoutePlannerTests.Map().Prototypes["pipe"];
        var target = new SpatialEntity("target", "load", new(.5, 4.5), new(new(.1, 4.1), new(.9, 4.9)), 0, "own",
            FluidConnections: [new(1, 1, new(.5, 4.5), new(.5, 3.5), Type: "normal", FlowDirection: "input", Filter: "water")]);
        map = map with { Entities = [target], Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { ["pipe"] = pipe },
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["pipe"] = new("pipe", 100) } };
        var plan = new OffshoreSupplyPlanner().Find(map, "pump", "pipe", "target", "water");
        Assert.Equal(expected, plan is not null);
        if (plan is not null)
        {
            Assert.Equal(PipeRouteStatus.Found, plan.Route.Status);
            Assert.Equal("target", plan.Route.Target!.EntityId);
            Assert.Equal(.5, plan.Pump.Position.Y);
        }
    }
}
