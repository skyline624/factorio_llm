using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryTransportFrameTests
{
    [Fact]
    public void DiagonalEndpointsBeyondTheOldDistanceLimitFitOneNativePhotograph()
    {
        var snapshot = Snapshot(("source", new(0.5, 0.5)), ("target", new(60.5, 60.5)));
        Assert.Equal(new MapPosition(30.5, 30.5), FactoryTransportBuilder.PlanningCenter(snapshot, ["source", "target"]));
    }

    [Fact]
    public void RetainedBusPartsDetermineTheFrameAlongWithTheNewEndpoints()
    {
        var snapshot = Snapshot(("source", new(0.5, 0.5)), ("target", new(50.5, 10.5)), ("tail", new(76.5, -10.5)));
        Assert.Equal(new MapPosition(38.5, 0), FactoryTransportBuilder.PlanningCenter(snapshot, ["source", "target", "tail"]));
    }

    [Theory]
    [InlineData(80, 0)]
    [InlineData(0, 80)]
    public void NativeFrameRetainsItsApproachAndRoutingMarginAtTheMaximumSpan(double x, double y)
    {
        var snapshot = Snapshot(("source", new(0, 0)), ("target", new(x, y)));
        Assert.Equal(new MapPosition(x / 2, y / 2), FactoryTransportBuilder.PlanningCenter(snapshot, ["source", "target"]));
    }

    [Theory]
    [InlineData(81, 0)]
    [InlineData(0, 81)]
    public void WiderGraphsRemainUnplannedInsteadOfInventingATerrainView(double x, double y)
    {
        var snapshot = Snapshot(("source", new(0, 0)), ("target", new(x, y)));
        Assert.Null(FactoryTransportBuilder.PlanningCenter(snapshot, ["source", "target"]));
    }

    [Fact]
    public void AnUnobservedBusPartCannotBeSubstitutedFromAPlannedPosition() =>
        Assert.Null(FactoryTransportBuilder.PlanningCenter(Snapshot(("source", new(0, 0))), ["source", "missing"]));

    [Fact]
    public void EmptyRequestsDoNotChooseAnArbitraryVantage() =>
        Assert.Null(FactoryTransportBuilder.PlanningCenter(Snapshot(), []));

    [Fact]
    public void DistantUnrelatedFactoryEntitiesDoNotEnlargeTheRequiredFrame()
    {
        var snapshot = Snapshot(("source", new(0, 0)), ("target", new(10, 0)), ("unrelated", new(1000, 1000)));
        Assert.Equal(new MapPosition(5, 0), FactoryTransportBuilder.PlanningCenter(snapshot, ["source", "target"]));
    }

    [Fact]
    public void SharedEndpointReferencesDoNotChangeTheChosenFrame()
    {
        var snapshot = Snapshot(("source", new(-20, 10)), ("target", new(20, -10)));
        Assert.Equal(new MapPosition(0, 0), FactoryTransportBuilder.PlanningCenter(snapshot, ["source", "source", "target"]));
    }

    private static FactorySnapshot Snapshot(params (string Id, MapPosition Position)[] entities) =>
        new("synthetic", new("world", "session", "actor", 1, 1), 100, 3700, Protocol.ToElement(new { }),
            entities.Select(e => new FactoryRecord(e.Id, "entity", e.Id, "iron-chest", Protocol.ToElement(new { position = e.Position }))).ToArray());
}
