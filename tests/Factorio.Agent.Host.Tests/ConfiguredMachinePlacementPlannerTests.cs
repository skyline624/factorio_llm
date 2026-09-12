using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ConfiguredMachinePlacementPlannerTests
{
    [Fact]
    public void CancellationStopsSpatialSearchWithoutReportingAnImpossibleLayout()
    {
        var map = PipeRoutePlannerTests.Map();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new PipeRoutePlanner().Find(map, "pipe", "source", "target", "oil",
            cancellationToken: cancelled.Token));
    }

    [Theory]
    [InlineData(true, 100, true)]
    [InlineData(false, 100, false)]
    [InlineData(true, 0, false)]
    public void PlacementRequiresObservedSupplyAndAnOwnedConnectedPowerPole(bool powered, double oil, bool expected)
    {
        var map = PipeRoutePlannerTests.Map();
        var prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
        {
            ["pole"] = map.Prototypes["wall"] with { Name = "pole", Type = "electric-pole", SupplyArea = 3.5 }
        };
        map = map with
        {
            Prototypes = prototypes,
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["machine"] = new("wall", 50) },
            Entities = [.. map.Entities, new("pole", "pole", new(3.5, 3.5),
                new(new(3, 3), new(4, 4)), 0, "agent", Power: new(0, powered ? 1 : null))]
        };
        var stock = new FactorySnapshot("s", map.Scope, 1, 100, Protocol.ToElement(new { }),
            [new("oil", "fluid", "oil", "fluid", Protocol.ToElement(new
            {
                aggregateSafe = true, contents = new Dictionary<string, double> { ["oil"] = oil },
                sourceBoxes = new[] { new { entityId = "source", index = 1 } }
            }))]);
        var plan = new ConfiguredMachinePlacementPlanner().Find(map, stock, "target", "machine", "pipe", ["oil"]);
        Assert.Equal(expected, plan is not null);
        if (plan is null) return;
        Assert.Equal("pole", plan.PoleId);
        Assert.Equal("source", Assert.Single(plan.Supplies).Supply.SourceId);
        Assert.Equal(ConfiguredMachinePlacementPlanner.PlannedId, plan.Supplies[0].Supply.Route.Target!.EntityId);
        Assert.True(plan.BuildApproach.DistanceTo(plan.Placement.Position) <= map.Actor.BuildDistance - 1);
    }

    [Fact]
    public void ProjectionPreservesNativeRecipeBoxIdentityWhileMovingPortsAndDisconnectingOldNeighbours()
    {
        var map = PipeRoutePlannerTests.Map();
        map = map with { Entities = map.Entities.Select(e => e.Id == "target" ? e with
            { FluidConnections = [e.FluidConnections![0] with { BoxIndex = 17 }] } : e with
            { FluidConnections = [e.FluidConnections![0] with { TargetEntityId = "target", TargetBoxIndex = 17 }] }).ToArray() };
        var projected = ConfiguredMachinePlacementPlanner.Project(map, "target", new(new(8, 8), 4, 0));
        var machine = projected.Entities.Single(e => e.Id == ConfiguredMachinePlacementPlanner.PlannedId);
        var port = Assert.Single(machine.FluidConnections!);
        Assert.Equal(17, port.BoxIndex);
        Assert.Equal("oil", port.Filter);
        Assert.Equal(new MapPosition(8, 8), port.Position);
        Assert.Equal(new MapPosition(8, 7), port.TargetPosition);
        Assert.Null(projected.Entities.Single(e => e.Id == "source").FluidConnections![0].TargetEntityId);
        Assert.DoesNotContain(projected.Entities, e => e.Id == "target");
    }
}
