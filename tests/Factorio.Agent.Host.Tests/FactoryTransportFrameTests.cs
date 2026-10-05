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
    public void CloserApproachCoversTheObservedNormalCoalAndPowerSeparation()
    {
        var snapshot = Snapshot(("source", new(-13.5, -17.5)), ("boiler-chest", new(70.5, -2.5)));
        Assert.Null(FactoryTransportBuilder.PlanningCenter(snapshot, ["source", "boiler-chest"]));
        Assert.Equal(new MapPosition(28.5, -10), FactoryTransportBuilder.PlanningCenter(snapshot, ["source", "boiler-chest"], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => FactoryTransportBuilder.PlanningCenter(snapshot, ["source"], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FactoryTransportBuilder.PlanningCenter(snapshot, ["source"], 5));
    }

    [Theory]
    [InlineData(86, 0, true)]
    [InlineData(0, 86, true)]
    [InlineData(87, 0, false)]
    [InlineData(0, 87, false)]
    public void CloserApproachStillRetainsTheNativePhotographRoutingMargin(double x, double y, bool fits)
    {
        var snapshot = Snapshot(("source", new(0, 0)), ("target", new(x, y)));
        var center = FactoryTransportBuilder.PlanningCenter(snapshot, ["source", "target"], 1);
        Assert.Equal(fits ? new MapPosition(x / 2, y / 2) : null, center);
    }

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

    [Fact]
    public void AnExistingCompletePhotographCoversEndpointsWithoutNeedingTheirCenter()
    {
        var map = FrameMap();
        Assert.True(FactoryTransportBuilder.FrameCovered(map, ["source", "target"]));
        Assert.False(new SpatialCollisionField(map).Walkable(new(0, 0)));
    }

    [Fact]
    public void MissingEndpointsAndInsufficientRoutingMarginCannotUseTheExistingPhotograph()
    {
        var map = FrameMap();
        Assert.False(FactoryTransportBuilder.FrameCovered(map, ["source", "missing"]));
        Assert.False(FactoryTransportBuilder.FrameCovered(map with { Bounds = new(new(-43, -48), new(48, 48)) }, ["source", "target"]));
        Assert.False(FactoryTransportBuilder.FrameCovered(map with { Coverage = map.Coverage with { Complete = false } }, ["source", "target"]));
    }

    [Fact]
    public void AnOccupiedCenterUsesWalkableGroundWithinTheNativeFrameTolerance()
    {
        var snapshot = Snapshot(("source", new(-42, 0)), ("target", new(42, 0)));
        var map = FrameMap() with { Scope = snapshot.Scope };
        var stand = FactoryTransportBuilder.PlanningStand(map, snapshot, ["source", "target"]);
        Assert.NotNull(stand);
        Assert.NotEqual(new MapPosition(0, 0), stand);
        Assert.InRange(stand.X, -1, 1);
        Assert.True(new SpatialCollisionField(map).Walkable(stand));
    }

    [Fact]
    public void AFrameWithNoKnownWalkableVantageIsDeferred()
    {
        var snapshot = Snapshot(("source", new(-42, 0)), ("target", new(42, 0)));
        var map = FactoryMaps.Grass(48, tile: (_, _) => "water") with { Scope = snapshot.Scope };
        Assert.Null(FactoryTransportBuilder.PlanningStand(map, snapshot, ["source", "target"]));
        Assert.Throws<InvalidDataException>(() => FactoryTransportBuilder.PlanningStand(map with
            { Scope = map.Scope with { Generation = map.Scope.Generation + 1 } }, snapshot, ["source", "target"]));
    }

    private static SpatialSnapshot FrameMap()
    {
        var map = FactoryMaps.Grass(48);
        var chest = map.Prototypes["iron-chest"];
        var tree = map.Prototypes["tree"];
        return map with { Entities = [new("source", chest.Name, new(-42, 0), chest.CollisionBox.Translate(new(-42, 0)), 0, "own"),
            new("target", chest.Name, new(42, 0), chest.CollisionBox.Translate(new(42, 0)), 0, "own"),
            new("obstacle", tree.Name, new(0, 0), tree.CollisionBox, 0, "neutral")] };
    }

    [Fact]
    public void ARemoteActorNeedsAReobservedOwnEndpointBeforeChoosingAWalkableFuelVantage()
    {
        var snapshot = Snapshot(("source", new(-12.5, -10.5)), ("target", new(66.5, -5.5)));
        var initial = FactoryMaps.Grass(48);
        var map = initial with { Scope = snapshot.Scope, Bounds = new(new(-120, -48), new(-24, 48)),
            Actor = initial.Actor with { Position = new(-72, 0) }, Rows = initial.Rows.Select(r => r with { X = r.X - 72 }).ToArray() };
        Assert.Null(FactoryTransportBuilder.PlanningStand(map, snapshot, ["source", "target"]));
        snapshot = snapshot with { Records = snapshot.Records.Select(r => r with
            { Data = Protocol.ToElement(new { role = "factory", position = FactoryTransportBuilder.Position(snapshot, r.EntityId) }) }).ToArray() };
        Assert.Equal("source", FactoryTransportBuilder.PlanningAnchors(snapshot, ["source", "target"], new(27, -8))[0]);
    }

    [Fact]
    public void FuelVantageTravelUsesOnlyKnownFactoryEndpoints()
    {
        var snapshot = Snapshot(("source", new(0, 0)), ("target", new(30, 0)), ("actor", new(15, 0)));
        snapshot = snapshot with { Records = snapshot.Records.Select(r => r with { Data = Protocol.ToElement(new
            { role = r.EntityId == "actor" ? "actor" : "factory", position = FactoryTransportBuilder.Position(snapshot, r.EntityId) }) }).ToArray() };
        Assert.Equal(["source", "target"], FactoryTransportBuilder.PlanningAnchors(snapshot, ["source", "source", "target", "actor", "missing"], new(15, 0)));
    }

    [Theory]
    [InlineData(94, true)]
    [InlineData(118, true)]
    [InlineData(119, false)]
    public void WiderFuelFramesRetainRoutingAndArrivalMargins(int span, bool fits)
    {
        var snapshot = Snapshot(("source", new(0, 0)), ("target", new(span, 0)));
        Assert.Null(FactoryTransportBuilder.PlanningCenter(snapshot, ["source", "target"], 1));
        var center = FactoryTransportBuilder.PlanningCenter(snapshot, ["source", "target"], 1, FactoryTransportBuilder.FuelPlanningRadius);
        if (fits) Assert.Equal(new MapPosition(span / 2.0, 0), center);
        else Assert.Null(center);
    }

    [Fact]
    public void WiderFuelVantageStillRequiresObservedWalkableGround()
    {
        var snapshot = Snapshot(("source", new(-50, 0)), ("target", new(50, 0)));
        var map = FactoryMaps.Grass(64) with { Scope = snapshot.Scope };
        Assert.Null(FactoryTransportBuilder.PlanningStand(map, snapshot, ["source", "target"]));
        Assert.Equal(new MapPosition(0, 0), FactoryTransportBuilder.PlanningStand(map, snapshot, ["source", "target"], 64));
        Assert.Null(FactoryTransportBuilder.PlanningStand(FactoryMaps.Grass(64, tile: (_, _) => "water") with { Scope = snapshot.Scope },
            snapshot, ["source", "target"], 64));
    }

    private static FactorySnapshot Snapshot(params (string Id, MapPosition Position)[] entities) =>
        new("synthetic", new("world", "session", "actor", 1, 1), 100, 3700, Protocol.ToElement(new { }),
            entities.Select(e => new FactoryRecord(e.Id, "entity", e.Id, "iron-chest", Protocol.ToElement(new { position = e.Position }))).ToArray());
}
