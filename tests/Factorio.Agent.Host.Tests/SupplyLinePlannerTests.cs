using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SupplyLinePlannerTests
{
    private static readonly ResourceCellEquipment Smelter = new("electric-mining-drill", "iron-chest", "stone-furnace", "inserter", "small-electric-pole");
    private static readonly SupplyLineEquipment Equipment = new("transport-belt", "inserter", "iron-chest", "small-electric-pole");

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void FeedersPickFromEveryChestAndDropOnOneStraightCollectorAlongTheWalkway(int direction)
    {
        var map = FactoryMaps.Grass(30);
        var row = Row(map, direction, cells: 3);
        map = Built(map, row);
        var access = ResourceCellPlanner.Access(map, row);
        var arm = map.Prototypes["inserter"];
        var collector = SupplyLinePlanner.Collector(map, row, Equipment, toward: new(100, 100))!;
        Assert.Equal([0, 1, 2], collector.Feeders.Select(f => f.Cell));
        var belts = collector.Belts.Select(b => b.Position).ToArray();
        foreach (var feeder in collector.Feeders)
        {
            Assert.Equal(SupplyLinePlanner.FeederRole(feeder.Cell), feeder.Inserter.Role);
            Assert.Equal(BeltRoutePlanner.Cell(access.Chests[feeder.Cell]), SupplyLinePlanner.PickupTile(arm, feeder.Inserter));
            Assert.Equal(feeder.Drop, SupplyLinePlanner.DropTile(arm, feeder.Inserter));
            Assert.Contains(feeder.Drop, belts);
            Assert.True(access.Walkway.Contains(feeder.Inserter.Position));
        }
        // One straight run in flow order: each belt feeds the next, the last one the trunk's head just beyond the walkway end.
        for (int i = 0; i + 1 < collector.Belts.Count; i++)
            Assert.Equal(collector.Belts[i + 1].Position, BeltRoutePlanner.Ahead(collector.Belts[i].Position, collector.Belts[i].Direction));
        Assert.Equal(collector.Head, BeltRoutePlanner.Ahead(collector.Belts[^1].Position, collector.Belts[^1].Direction));
        Assert.All(collector.Belts, b => Assert.True(access.Walkway.Contains(b.Position)));
        Assert.False(access.Walkway.Contains(collector.Head));
        Assert.Equal(collector.Belts.Select((_, i) => SupplyLinePlanner.CollectorRole(i)), collector.Belts.Select(b => b.Role));
        // Nothing planned overlaps the built row.
        var field = new SpatialCollisionField(map);
        Assert.All(collector.Belts, b => Assert.True(field.PlacementClear(map.Prototypes["transport-belt"], b.Position, b.Direction)));
        Assert.All(collector.Feeders, f => Assert.True(field.PlacementClear(arm, f.Inserter.Position, f.Inserter.Direction)));
    }

    [Fact]
    public void TheCollectorFlowsToTheWalkwayEndNearerTheDepotUnlessThatEndIsBlocked()
    {
        var map = FactoryMaps.Grass(30);
        // Facing north, the row runs east-west.
        var row = Row(map, 0, cells: 3);
        map = Built(map, row);
        var east = SupplyLinePlanner.Collector(map, row, Equipment, new(100, 0))!;
        var west = SupplyLinePlanner.Collector(map, row, Equipment, new(-100, 0))!;
        Assert.All(east.Belts, b => Assert.Equal(4, b.Direction));
        Assert.All(west.Belts, b => Assert.Equal(12, b.Direction));
        Assert.True(east.Head.X > west.Head.X);
        var blocked = map with { Entities = [.. map.Entities, Chest("blocker", east.Head)] };
        Assert.All(SupplyLinePlanner.Collector(blocked, row, Equipment, new(100, 0))!.Belts, b => Assert.Equal(12, b.Direction));
        // A building on the collector itself leaves no collector at all.
        var cut = map with { Entities = [.. map.Entities, Chest("cut", east.Feeders[1].Drop)] };
        Assert.Null(SupplyLinePlanner.Collector(cut, row, Equipment, new(100, 0)));
    }

    [Fact]
    public void ACellWhoseFeederTileIsTakenStaysOutOfTheLine()
    {
        var map = FactoryMaps.Grass(30);
        var row = Row(map, 8, cells: 3);
        map = Built(map, row);
        var access = ResourceCellPlanner.Access(map, row);
        var taken = new MapPosition(access.Chests[1].X + access.Outward.X, access.Chests[1].Y + access.Outward.Y);
        var collector = SupplyLinePlanner.Collector(map with { Entities = [.. map.Entities, Chest("stranger", taken)] }, row, Equipment, new(0, 100))!;
        Assert.Equal([0, 2], collector.Feeders.Select(f => f.Cell));
        Assert.Contains(BeltRoutePlanner.Cell(new(access.Chests[1].X + 2 * access.Outward.X, access.Chests[1].Y + 2 * access.Outward.Y)),
            collector.Belts.Select(b => b.Position));
    }

    [Fact]
    public void TheDepotStandsBesideTheBandWalkwayEndNearerTheRowOutsideTheBandAndItsCorridors()
    {
        var map = FactoryMaps.Grass(40);
        var zone = new FactoryZone(1, new(-12, -6), 4, 3, 12);
        var walkway = FactoryBandPlanner.Walkway(zone.Origin, zone.Slots, zone.Pitch, zone.BandHeight);
        var reserved = FactoryCellBuilder.ReserveZone(map, zone, "small-electric-pole");
        var row = new MapPosition(-38, 0);
        var depot = SupplyLinePlanner.Depot(reserved, Equipment, walkway, row, new HashSet<string>())!;
        var end = SupplyLinePlanner.End(walkway, row);
        Assert.Equal(new MapPosition(walkway.Min.X - .5, 0), end);
        Assert.True(depot.Chest.Position.DistanceTo(end) <= SupplyLinePlanner.DepotRadius);
        var arm = map.Prototypes["inserter"];
        Assert.Equal(BeltRoutePlanner.Cell(depot.Chest.Position), SupplyLinePlanner.DropTile(arm, depot.Inserter));
        Assert.Equal(depot.Pickup, SupplyLinePlanner.PickupTile(arm, depot.Inserter));
        foreach (var at in new[] { depot.Chest.Position, depot.Inserter.Position, depot.Pickup })
        {
            Assert.False(zone.Box.Contains(at));
            Assert.DoesNotContain(SupplyLinePlanner.Corridors(walkway), corridor => corridor.Contains(at));
        }
        Assert.Equal((SupplyLinePlanner.DepotChestRole, SupplyLinePlanner.DepotInserterRole), (depot.Chest.Role, depot.Inserter.Role));
        Assert.False(depot.Powered);
        // The east end serves a row east of the band.
        Assert.Equal(new MapPosition(walkway.Max.X + .5, 0), SupplyLinePlanner.End(walkway, new(38, 0)));
    }

    [Fact]
    public void ADepotAFedPoleAlreadyPowersComesFirst()
    {
        var zone = new FactoryZone(1, new(-12, -6), 4, 3, 12);
        var walkway = FactoryBandPlanner.Walkway(zone.Origin, zone.Slots, zone.Pitch, zone.BandHeight);
        var box = new WorldBox(new(-0.1484375, -0.1484375), new(0.1484375, 0.1484375));
        var fed = new SpatialEntity("fed", "small-electric-pole", new(-16.5, -3.5), box.Translate(new(-16.5, -3.5)), 0, "agent", Power: new(1, 7));
        var map = FactoryCellBuilder.ReserveZone(FactoryMaps.Grass(40, [fed]), zone, "small-electric-pole");
        var depot = SupplyLinePlanner.Depot(map, Equipment, walkway, new(-38, 0), new HashSet<string> { "fed" })!;
        Assert.True(depot.Powered);
        var arm = map.Prototypes["inserter"];
        Assert.True(PowerGridPlanner.Supplies(fed.Position, map.Prototypes["small-electric-pole"],
            arm.CollisionBox.Rotate(depot.Inserter.Direction).Translate(depot.Inserter.Position)));
        // An unfed pole powers nothing.
        Assert.False(SupplyLinePlanner.Depot(map, Equipment, walkway, new(-38, 0), new HashSet<string>())!.Powered);
    }

    [Fact]
    public void ATrunkSegmentContinuesItsBeltToAVisibleDepotOrStopsNearTheEdgeTowardADistantOne()
    {
        var map = FactoryMaps.Grass(30, [Belt("previous", new(0.5, 0.5), 4)]);
        var head = new MapPosition(1.5, 0.5);
        var target = new MapPosition(12.5, 6.5);
        var found = SupplyLinePlanner.Segment(map, "transport-belt", head, 4, "previous", target)!;
        Assert.Null(found.Next);
        Assert.Equal(head, found.Belts[0].Position);
        Assert.Equal(target, found.Belts[^1].Position);
        for (int i = 0; i + 1 < found.Belts.Count; i++)
            Assert.Equal(found.Belts[i + 1].Position, BeltRoutePlanner.Ahead(found.Belts[i].Position, found.Belts[i].Direction));
        var partial = SupplyLinePlanner.Segment(map, "transport-belt", head, 4, "previous", new(400.5, 0.5))!;
        Assert.NotNull(partial.Next);
        Assert.True(map.Bounds.Max.X - partial.Next.X < BeltRoutePlanner.EdgeMargin);
        Assert.Equal(partial.Next, BeltRoutePlanner.Ahead(partial.Belts[^1].Position, partial.Belts[^1].Direction));
        // A trunk of a single tile keeps the incoming direction.
        Assert.Equal([new PlacementCandidate(head, 4, 0)], SupplyLinePlanner.Segment(map, "transport-belt", head, 4, "previous", head)!.Belts);
        // Only the belt the trunk continues may touch the head: any other belt keeps its neighbourhood free.
        Assert.Null(SupplyLinePlanner.Segment(map, "transport-belt", head, 4, "stranger", target));
    }

    internal static ResourceRow Row(SpatialSnapshot map, int direction, int cells) =>
        new(1, "smelter", "iron-plate", "iron-ore", Smelter, new(0, 0), direction,
            ResourceCellPlanner.Pitch(map, Smelter, direction) ?? throw new InvalidDataException("No template."), cells, 18.75);

    /// <summary>The row's planned cells as built entities, inserters with their native pickup and drop points.</summary>
    internal static SpatialSnapshot Built(SpatialSnapshot map, ResourceRow row) => map with
    {
        Entities = [.. map.Entities, .. Enumerable.Range(0, row.Cells).SelectMany(i => new ResourceCellPlanner().Layout(map, row, i).Entities)
            .Select((e, n) => Entity(map, $"built-{n}", e))]
    };

    private static SpatialEntity Entity(SpatialSnapshot map, string id, PlannedEntity planned)
    {
        var geometry = map.Prototypes[map.Items[planned.Item].EntityName];
        var bounds = geometry.CollisionBox.Rotate(planned.Direction).Translate(planned.Position);
        MapPosition? At(MapPosition? vector) => vector is null ? null
            : new(planned.Position.X + ExtractionPlanner.Rotate(vector, planned.Direction).X, planned.Position.Y + ExtractionPlanner.Rotate(vector, planned.Direction).Y);
        return new(id, geometry.Name, planned.Position, bounds, planned.Direction, "agent",
            DropPosition: At(geometry.InserterDrop), PickupPosition: At(geometry.InserterPickup));
    }

    private static SpatialEntity Chest(string id, MapPosition at) =>
        new(id, "iron-chest", at, new(new(at.X - .35, at.Y - .35), new(at.X + .35, at.Y + .35)), 0, "agent");

    internal static SpatialEntity Belt(string id, MapPosition at, int direction) =>
        new(id, "transport-belt", at, new(new(at.X - .4, at.Y - .4), new(at.X + .4, at.Y + .4)), direction, "agent");
}
