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

    [Fact]
    public void AnExistingNativeLineRetainsItsEndpointsAndCoordinatesUntilAHostRetiresTheReplacedBelt()
    {
        var original = OriginalLine();
        var plan = new BalancedBeltPlanner().FindExisting(original.Map, new("belt", "arm", "pole"), "splitter",
            "source", "target", "second", "old-source-arm", "old-first-arm", original.Belts, original.Poles,
            new HashSet<string>(StringComparer.Ordinal));
        Assert.NotNull(plan);
        Assert.Equal(original.Plan.SourceInserter.Position, plan.First.SourceInserter.Position);
        Assert.Equal(original.Plan.TargetInserter.Position, plan.First.TargetInserter.Position);
        Assert.Equal(original.Plan.Belts.Select(p => p.Position), plan.First.Belts.Select(p => p.Position));
        Assert.InRange(plan.ReplacedBelt, 1, original.Belts.Length - 2);
        Assert.Equal(original.Plan.Belts[plan.ReplacedBelt].Position,
            original.Map.Entities.Single(e => e.Id == original.Belts[plan.ReplacedBelt]).Position);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("hidden-input")]
    [InlineData("wrong-pickup")]
    [InlineData("foreign-tail")]
    [InlineData("protected-endpoint")]
    public void ConversionCannotProjectAwayUnverifiedTransportOrEndpointEntities(string change)
    {
        var original = OriginalLine();
        var map = original.Map;
        var obsolete = new HashSet<string>(StringComparer.Ordinal);
        if (change == "protected-endpoint") obsolete.Add("target");
        map = map with { Entities = map.Entities.Select(e => change switch
        {
            "foreign" when e.Id == original.Belts[1] => e with { Force = "foreign" },
            "hidden-input" when e.Id == original.Belts[1] => e with
                { BeltConnections = e.BeltConnections! with { InputsCount = 2 } },
            "wrong-pickup" when e.Id == "old-first-arm" => e with { PickupTargetId = original.Belts[0] },
            "foreign-tail" when e.Id == original.Belts[^1] => e with
                { BeltConnections = e.BeltConnections! with { Outputs = ["unknown-native-belt"], OutputsCount = 1 } },
            _ => e
        }).ToArray() };
        Assert.Throws<InvalidDataException>(() => new BalancedBeltPlanner().FindExisting(map,
            new("belt", "arm", "pole"), "splitter", "source", "target", "second", "old-source-arm", "old-first-arm",
            original.Belts, original.Poles, obsolete));
    }

    // Synthetic original native line with exact adjacency and endpoint observations; no mutation is performed by the planner.
    internal static (SpatialSnapshot Map, BeltTransportPlan Plan, string[] Belts, string[] Poles) OriginalLine(bool extend = false)
    {
        var map = Map();
        var plan = new BeltTransportPlanner().Find(map, new("belt", "arm", "pole"), "source", "target")!;
        Assert.True(plan.Belts.Count >= 3);
        string[] ids = Enumerable.Range(0, plan.Belts.Count).Select(i => $"old-belt-{i}").ToArray();
        SpatialEntity Native(string id, string item, PlacementCandidate piece) => new(id, item, piece.Position,
            map.Prototypes[item].CollisionBox.Rotate(piece.Direction).Translate(piece.Position), piece.Direction, "own");
        var belts = plan.Belts.Select((p, i) => Native(ids[i], "belt", p) with { BeltConnections = new(
            i == 0 ? [] : [ids[i - 1]], i + 1 == ids.Length ? [] : [ids[i + 1]], i == 0 ? 0 : 1, i + 1 == ids.Length ? 0 : 1) });
        string[] poles = Enumerable.Range(0, plan.Poles.Count).Select(i => $"old-pole-{i}").ToArray();
        map = map with { Entities = [.. map.Entities, .. belts,
            Native("old-source-arm", "arm", plan.SourceInserter) with { PickupTargetId = "source", DropTargetId = ids[0] },
            Native("old-first-arm", "arm", plan.TargetInserter) with { PickupTargetId = ids[^1], DropTargetId = "target" },
            .. plan.Poles.Select((p, i) => Native(poles[i], "pole", p) with { Power = new(100, 1, 10) })] };
        if (extend)
        {
            var extension = new FactoryBeltPlanner().Extend(map, new("belt", "arm", "pole"), ids, "second");
            Assert.NotNull(extension);
            var allIds = ids.Concat(Enumerable.Range(1, extension.Belts.Count - 1).Select(i => $"old-extension-{i}")).ToArray();
            var positions = plan.Belts.Take(plan.Belts.Count - 1).Concat(extension.Belts).ToArray();
            var allPoles = poles.Concat(Enumerable.Range(0, extension.Poles.Count).Select(i => $"old-extension-pole-{i}")).ToArray();
            var beltSet = ids.ToHashSet(StringComparer.Ordinal);
            map = map with { Entities = [.. map.Entities.Where(e => !beltSet.Contains(e.Id)),
                .. positions.Select((p, i) => Native(allIds[i], "belt", p) with { BeltConnections = new(
                    i == 0 ? [] : [allIds[i - 1]], i + 1 == allIds.Length ? [] : [allIds[i + 1]], i == 0 ? 0 : 1, i + 1 == allIds.Length ? 0 : 1) }),
                Native("old-second-arm", "arm", extension.TargetInserter) with { PickupTargetId = allIds[^1], DropTargetId = "second" },
                .. extension.Poles.Select((p, i) => Native(allPoles[poles.Length + i], "pole", p) with { Power = new(100, 1, 10) })] };
            plan = plan with { Belts = positions.Take(ids.Length).ToArray(), Poles = plan.Poles.Concat(extension.Poles).ToArray() };
            ids = allIds;
            poles = allPoles;
        }
        return (map, plan, ids, poles);
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
