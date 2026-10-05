using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class BeltTransportPlannerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ElectricExtensionsLeaveBothFutureBeltPortsAvailable(bool reverseEndpoints)
    {
        var map = Map(true);
        var pole = map.Prototypes["pole"];
        var wall = map.Prototypes["wall"];
        var blockers = new[] { new MapPosition(.5, -.5), new(-.5, .5), new(1.5, .5), new(-.5, 1.5), new(1.5, 1.5) }
            .Select((p, index) => new SpatialEntity($"wall-{index}", "wall", p, wall.CollisionBox.Translate(p), 0, "own"));
        // The chest's only free arm stands south. Its old pole does not cover that arm, and the nearest new
        // pole position is exactly the arm's future belt port. Another powered placement and belt route exist.
        map = map with { Entities = [.. map.Entities.Where(e => e.Id != "pole1"),
            new("pole1", "pole", new(.5, 5.5), pole.CollisionBox.Translate(new(.5, 5.5)), 0, "own", Power: new(0, 1)), .. blockers] };
        var plan = new BeltTransportPlanner().Find(map, new("belt", "arm", "pole"),
            reverseEndpoints ? "target" : "source", reverseEndpoints ? "source" : "target");
        Assert.NotNull(plan);
        Assert.NotEmpty(plan.Poles);
        Assert.Equal(new(.5, 1.5), reverseEndpoints ? plan.TargetInserter.Position : plan.SourceInserter.Position);
        var belt = map.Prototypes["belt"];
        Assert.All(plan.Poles, p => Assert.DoesNotContain(plan.Belts,
            b => pole.CollisionBox.Translate(p.Position).Overlaps(belt.CollisionBox.Rotate(b.Direction).Translate(b.Position))));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void CompleteLayoutUsesNativePickupDropAndAvailableElectricSupply(bool power, bool expected)
    {
        var map = Map(power);
        var plan = new BeltTransportPlanner().Find(map, new("belt", "arm", "pole"), "source", "target");
        Assert.Equal(expected, plan is not null);
        if (plan is null) return;
        var arm = map.Prototypes["arm"];
        MapPosition At(PlacementCandidate p, MapPosition offset)
        {
            var rotated = ExtractionPlanner.Rotate(offset, p.Direction);
            return new(p.Position.X + rotated.X, p.Position.Y + rotated.Y);
        }
        Assert.True(map.Entities.Single(e => e.Id == "source").Bounds.Contains(At(plan.SourceInserter, arm.InserterPickup!)));
        Assert.Equal(plan.Belts[0].Position, BeltRoutePlanner.Cell(At(plan.SourceInserter, arm.InserterDrop!)));
        Assert.Equal(plan.Belts[^1].Position, BeltRoutePlanner.Cell(At(plan.TargetInserter, arm.InserterPickup!)));
        Assert.True(map.Entities.Single(e => e.Id == "target").Bounds.Contains(At(plan.TargetInserter, arm.InserterDrop!)));
    }

    [Fact]
    public void NativeLongReachPortsAreFilteredBeforeTheCandidateLimit()
    {
        var map = Map(true);
        map = map with
        {
            Bounds = new(new(-32, -32), new(33, 33)),
            Rows = Enumerable.Range(-32, 65).Select(y => new TileRun(-32, y, 65, map.Rows[0].Name)).ToArray(),
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            {
                ["arm"] = map.Prototypes["arm"] with { InserterPickup = new(0, -8), InserterDrop = new(0, 8.2) },
                ["pole"] = map.Prototypes["pole"] with { SupplyArea = 32 }
            }
        };
        var plan = new BeltTransportPlanner().Find(map, new("belt", "arm", "pole"), "source", "target");
        Assert.NotNull(plan);
        var pickup = ExtractionPlanner.Rotate(map.Prototypes["arm"].InserterPickup!, plan.SourceInserter.Direction);
        var drop = ExtractionPlanner.Rotate(map.Prototypes["arm"].InserterDrop!, plan.TargetInserter.Direction);
        Assert.True(map.Entities.Single(e => e.Id == "source").Bounds.Contains(
            new MapPosition(plan.SourceInserter.Position.X + pickup.X, plan.SourceInserter.Position.Y + pickup.Y)));
        Assert.True(map.Entities.Single(e => e.Id == "target").Bounds.Contains(
            new MapPosition(plan.TargetInserter.Position.X + drop.X, plan.TargetInserter.Position.Y + drop.Y)));
    }

    [Fact]
    public void AFirstPortRequiringATunnelDoesNotHideALaterOrdinaryLayout()
    {
        var map = Map(true);
        var wall = map.Prototypes["wall"];
        var pocket = new[] { new MapPosition(2.5, -.5), new(2.5, 1.5), new(3.5, .5) }
            .Select((p,i) => new SpatialEntity($"pocket-{i}", "wall", p, wall.CollisionBox.Translate(p), 0, "own"));
        map = map with
        {
            Prototypes = new Dictionary<string,EntityGeometry>(map.Prototypes)
            {
                ["pole"] = map.Prototypes["pole"] with { SupplyArea = 10 },
                ["underground"] = map.Prototypes["belt"] with { Name = "underground", Type = "underground-belt", MaxUndergroundDistance = 2 }
            },
            Items = new Dictionary<string,PlaceableItem>(map.Items) { ["underground"] = new("underground",50) },
            Entities = [.. map.Entities, .. pocket]
        };
        var ordinary = new BeltTransportPlanner().Find(map,new("belt","arm","pole"),"source","target");
        var optional = new BeltTransportPlanner().Find(map,new("belt","arm","pole","underground"),"source","target");
        Assert.NotNull(ordinary);
        Assert.NotNull(optional);
        Assert.Equal(ordinary.SourceInserter,optional.SourceInserter);
        Assert.Equal(ordinary.TargetInserter,optional.TargetInserter);
        Assert.Equal(ordinary.Belts,optional.Belts);
        Assert.Null(optional.UndergroundBeltItem);
    }

    [Fact]
    public void ANodeExhaustedAssignmentLeavesOtherBoundedBatchSearchesAvailable()
    {
        var map = Map(true);
        MapPosition alternative = new(12.5,.5);
        map = map with { Entities = [..map.Entities,
            new("alternative","chest",alternative,map.Prototypes["chest"].CollisionBox.Translate(alternative),0,"own")] };
        var batch = new BeltTransportBatchPlanner().Find(map,new("belt","arm","pole"),
            [new BeltTransportRequest("target",["source","alternative"])],maximumSearches:2,nodeBudget:1);
        Assert.Equal("alternative",Assert.Single(batch.Links).SourceId);
        Assert.True(batch.BudgetExhausted);
        Assert.Equal(2,batch.Searches);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AWholeBatchReservesTheDestinationWithOneNativeMaterialPortFirst(int blockerDistance)
    {
        var map = Map(true);
        var wall = map.Prototypes["wall"];
        MapPosition secondSource = new(.5,8.5), constrained = new(8.5,8.5);
        var blockers = new[] { new MapPosition(8.5,8.5-blockerDistance),new(8.5,8.5+blockerDistance),new(8.5+blockerDistance,8.5) }
            .Select((p,i)=>new SpatialEntity($"tight-{i}","wall",p,wall.CollisionBox.Translate(p),0,"own"));
        map = map with
        {
            Prototypes = new Dictionary<string,EntityGeometry>(map.Prototypes) { ["pole"] = map.Prototypes["pole"] with { SupplyArea = 32 } },
            Entities = [..map.Entities,
                new("second-source","chest",secondSource,map.Prototypes["chest"].CollisionBox.Translate(secondSource),0,"own"),
                new("constrained","chest",constrained,map.Prototypes["chest"].CollisionBox.Translate(constrained),0,"own"),..blockers]
        };
        var batch = new BeltTransportBatchPlanner().Find(map,new("belt","arm","pole"),
            [new BeltTransportRequest("target",["source"]),new("constrained",["second-source"])],stopAfterComplete:true);
        Assert.Equal(2,batch.Links.Count);
        Assert.Equal("constrained",batch.Links[0].TargetId);
        Assert.False(batch.BudgetExhausted);
    }

    internal static SpatialSnapshot Map(bool power)
    {
        var map = BeltRoutePlannerTests.Map();
        return map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            {
                ["arm"] = new("arm", "inserter", new(new(-.15, -.15), new(.15, .15)), map.Prototypes["wall"].Mask, 1, 1,
                    IsElectric: true, InserterPickup: new(0, -1), InserterDrop: new(0, 1.2)),
                ["pole"] = new("pole", "electric-pole", new(new(-.15, -.15), new(.15, .15)), map.Prototypes["wall"].Mask, 1, 1,
                    SupplyArea: 3.5, MaxWireDistance: 7.5)
            },
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["arm"] = new("arm", 50), ["pole"] = new("pole", 50) },
            Entities =
            [new("source", "chest", new(.5, .5), new(new(.15, .15), new(.85, .85)), 0, "own"),
             new("target", "chest", new(8.5, .5), new(new(8.15, .15), new(8.85, .85)), 0, "own"),
             new("pole1", "pole", new(1.5, 3.5), new(new(1.35, 3.35), new(1.65, 3.65)), 0, "own", Power: new(0, power ? 1 : null)),
             new("pole2", "pole", new(7.5, 3.5), new(new(7.35, 3.35), new(7.65, 3.65)), 0, "own", Power: new(0, power ? 1 : null))]
        };
    }
}
