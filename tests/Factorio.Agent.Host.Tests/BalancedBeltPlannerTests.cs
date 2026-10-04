using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class BalancedBeltPlannerTests
{
    [Fact]
    public void ANewLineBranchesBeforeEitherConsumerAndPersistsBothPaths()
    {
        var map = Map();
        var plan = new BalancedBeltPlanner().Find(map, new("belt", "arm", "pole"), "splitter", "source", "target", "second");
        Assert.NotNull(plan);
        var ports = BalancedBeltPlanner.Ports(map.Prototypes["splitter"], plan.Splitter);
        Assert.Contains(plan.First.Belts[plan.ReplacedBelt + 1].Position, ports.Outputs);
        Assert.Contains(plan.Second.Belts[0].Position, ports.Outputs);
        Assert.NotEqual(plan.First.Belts[plan.ReplacedBelt + 1].Position, plan.Second.Belts[0].Position);
        var record = FactoryTransportBuilder.NewBalancedBus("source", "target", "second", "iron-plate", 40, 80, plan, 10);
        Assert.Equal("building", record.Cell.Status);
        Assert.Empty(record.Cell.Entities);
        Assert.Equal(2, record.Bus.Consumers.Count);
        Assert.Equal(2, record.Bus.Graph!["splitter-0"].Outputs.Count);
        Assert.Equal(2, record.Bus.Graph.Count(p => p.Value.Outputs.Count == 0));
        Assert.Equal(plan.First.Belts.Count - 1 + plan.Second.Belts.Count,
            record.Cell.Plan!.Values.Count(p => p.Item == "transport-belt"));
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(4, 0, 1)]
    [InlineData(8, -1, 0)]
    [InlineData(12, 0, -1)]
    public void NativeTwoWidePortsRotateWithCardinalFlow(int direction, double acrossX, double acrossY)
    {
        var map = Map();
        var center = ExtractionPlanner.Rotate(new(4, 2.5), direction);
        var ports = BalancedBeltPlanner.Ports(map.Prototypes["splitter"], new(center, direction, 0));
        Assert.Equal(new(acrossX, acrossY), new MapPosition(ports.Outputs[1].X - ports.Outputs[0].X,
            ports.Outputs[1].Y - ports.Outputs[0].Y));
        Assert.All(ports.Inputs.Concat(ports.Outputs), p => Assert.Equal(p, BeltRoutePlanner.Cell(p)));
    }

    [Fact]
    public void MatchingSpeedAndKnownNativeFootprintAreRequired()
    {
        var map = Map();
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["splitter"] = map.Prototypes["splitter"] with { BeltSpeed = .0625 } } };
        Assert.Throws<InvalidDataException>(() => new BalancedBeltPlanner().Find(map, new("belt", "arm", "pole"),
            "splitter", "source", "target", "second"));
    }

    [Fact]
    public void DuplicateConsumersAreRejectedBeforePlanning()
    {
        Assert.Throws<ArgumentException>(() => new BalancedBeltPlanner().Find(Map(), new("belt", "arm", "pole"),
            "splitter", "source", "target", "target"));
    }

    internal static SpatialSnapshot Map()
    {
        var map = BeltTransportPlannerTests.Map(true);
        return map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            {
                ["arm"] = map.Prototypes["arm"] with { FilterSlots = 5 },
                ["splitter"] = new("splitter", "splitter", new(new(-.9, -.4), new(.9, .4)),
                    map.Prototypes["belt"].Mask, 2, 1, BeltSpeed: map.Prototypes["belt"].BeltSpeed)
            },
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["splitter"] = new("splitter", 50) },
            Entities = [.. map.Entities, new("second", "chest", new(8.5, 6.5), new(new(8.15, 6.15), new(8.85, 6.85)), 0, "own")]
        };
    }
}
