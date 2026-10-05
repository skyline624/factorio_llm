using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class LongBeltTransportPlannerTests
{
    [Fact]
    public void ALongSurveyedConnectionUsesAnExplicitLargerConstructionBudget()
    {
        var map = LongMap();
        var plan = new BeltTransportPlanner().Find(map, new("belt", "arm", "pole"), "source", "target", maximumBelts: 1000);
        Assert.NotNull(plan);
        Assert.InRange(plan.Belts.Count, 390, 450);
        Assert.True(plan.Belts.Zip(plan.Belts.Skip(1)).All(p => Math.Abs(p.First.Position.X - p.Second.Position.X)
            + Math.Abs(p.First.Position.Y - p.Second.Position.Y) == 1));
    }

    [Fact]
    public void OrdinaryCallsKeepTheirTwoHundredBeltBudget()
    {
        Assert.Null(new BeltTransportPlanner().Find(LongMap(), new("belt", "arm", "pole"), "source", "target"));
    }

    [Fact]
    public void JointAssignmentAcceptsAnExplicitLongRouteWithoutIncreasingOrdinaryBounds()
    {
        var map = LongMap();
        var equipment = new BeltTransportEquipment("belt", "arm", "pole");
        BeltTransportRequest[] requests = [new("target", ["source"])];
        Assert.Empty(new BeltTransportBatchPlanner().Find(map, equipment, requests).Links);
        var batch = new BeltTransportBatchPlanner().Find(map, equipment, requests, stopAfterComplete: true,
            maximumBelts: 512, nodeBudget: 24000);
        var link = Assert.Single(batch.Links);
        Assert.InRange(link.Plan.Belts.Count, 390, 450);
        Assert.False(batch.BudgetExhausted);
    }

    private static SpatialSnapshot LongMap()
    {
        var map = BeltTransportPlannerTests.Map(true);
        return map with
        {
            Bounds = new(new(-10, -10), new(431, 11)),
            Rows = Enumerable.Range(-10, 21).Select(y => new TileRun(-10, y, 441, map.Rows[0].Name)).ToArray(),
            Entities = map.Entities.Select(e => e.Id is "target" or "pole2"
                ? e with { Position = new(e.Position.X + 412, e.Position.Y), Bounds = e.Bounds.Translate(new(412, 0)) } : e).ToArray(),
            Coverage = new(false, false, "historical-character-survey-with-blocked-unknown-tiles", 48)
        };
    }
}
