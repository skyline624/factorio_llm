using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryBeltPlannerTests
{
    [Fact]
    public void BusExtensionRetainsExistingConsumerPickupAndOnlyReorientsTheTail()
    {
        var map = BeltTransportPlannerTests.Map(true);
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { ["arm"] = map.Prototypes["arm"] with { FilterSlots = 5 } } };
        var equipment = new BeltTransportEquipment("belt", "arm", "pole");
        var first = new BeltTransportPlanner().Find(map, equipment, "source", "target")!;
        SpatialEntity Placed(string id, string name, PlacementCandidate p) => new(id, name, p.Position,
            map.Prototypes[name].CollisionBox.Rotate(p.Direction).Translate(p.Position), p.Direction, "own", Power: new(1000, 1));
        MapPosition At(PlacementCandidate p, MapPosition offset)
        {
            var rotated = ExtractionPlanner.Rotate(offset, p.Direction);
            return new(p.Position.X + rotated.X, p.Position.Y + rotated.Y);
        }
        string[] ids = Enumerable.Range(0, first.Belts.Count).Select(i => $"b{i}").ToArray();
        var belts = first.Belts.Select((p, i) => Placed(ids[i], "belt", p) with
            { BeltConnections = new(i == 0 ? [] : [ids[i - 1]], i + 1 == ids.Length ? [] : [ids[i + 1]], i == 0 ? 0 : 1, i + 1 == ids.Length ? 0 : 1) });
        map = map with { Entities = [.. map.Entities, .. belts,
            Placed("extractor", "arm", first.SourceInserter) with { PickupTargetId = "source", DropTargetId = ids[0],
                PickupPosition = At(first.SourceInserter, map.Prototypes["arm"].InserterPickup!), DropPosition = At(first.SourceInserter, map.Prototypes["arm"].InserterDrop!) },
            Placed("receiver", "arm", first.TargetInserter) with { PickupTargetId = ids[^1], DropTargetId = "target",
                PickupPosition = At(first.TargetInserter, map.Prototypes["arm"].InserterPickup!), DropPosition = At(first.TargetInserter, map.Prototypes["arm"].InserterDrop!) },
            new("third", "chest", new(8.5, 6.5), new(new(8.15, 6.15), new(8.85, 6.85)), 0, "own")] };
        var next = new FactoryBeltPlanner().Extend(map, equipment, ids, "third");
        Assert.NotNull(next);
        Assert.Equal(first.Belts[^1].Position, next.Belts[0].Position);
        Assert.DoesNotContain(next.Belts.Skip(1), p => first.Belts.Any(old => old.Position == p.Position));
        Assert.True(map.Entities.Single(e => e.Id == "third").Bounds.Contains(At(next.TargetInserter, map.Prototypes["arm"].InserterDrop!)));
        Assert.Equal(ids[^1], map.Entities.Single(e => e.Id == "receiver").PickupTargetId);
    }

    [Fact]
    public void InletExceptionDoesNotAcceptAnUnrelatedBeltBranch()
    {
        var map = BeltRoutePlannerTests.Map();
        map = map with { Entities = [new("inlet", "belt", new(.5, .5), new(new(.1, .1), new(.9, .9)), 4, "own"),
            new("foreign", "belt", new(1.5, 1.5), new(new(1.1, 1.1), new(1.9, 1.9)), 0, "own")] };
        var route = new BeltRoutePlanner().Find(map, "belt", new(1.5, .5), new(4.5, .5), inletBeltId: "inlet");
        Assert.Equal(BeltRouteStatus.NoRouteInSnapshot, route.Status);
    }
}
