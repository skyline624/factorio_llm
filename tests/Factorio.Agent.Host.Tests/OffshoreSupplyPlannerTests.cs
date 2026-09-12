using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class OffshoreSupplyPlannerTests
{
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
