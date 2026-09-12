using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FuelFeederPlannerTests
{
    [Theory]
    [InlineData("chest")]
    [InlineData("arm")]
    public void MissingNativeStockRecordsCannotLookLikeSuccessfulDelivery(string missing)
    {
        FactoryRecord[] records = [new("inventory", "inventory", "chest", "chest", Protocol.ToElement(new { items = new { wood = 10 } })),
            new("held", "transit", "arm", "inserter-hand", Protocol.ToElement(new { items = new { wood = 1 } }))];
        var snapshot = new FactorySnapshot("s", new("w", "s", "a", 1, 1), 1, 100, Protocol.ToElement(new { }),
            records.Where(r => r.EntityId != missing).ToArray());
        Assert.Throws<InvalidDataException>(() => FeederStockReading.From(snapshot, "chest", "arm", "wood"));
    }

    [Theory]
    [InlineData("boiler", "old-arm")]
    [InlineData("other-target", null)]
    public void ReuseRequiresTheActualPickupAndDropEntities(string target, string? expectedArm)
    {
        var map = Map();
        var chest = new SpatialEntity("old-chest", "chest", new(2.5, .5), new(new(2.15, .15), new(2.85, .85)), 0, "agent");
        var arm = new SpatialEntity("old-arm", "inserter", new(3.5, .5), new(new(3.35, .35), new(3.65, .65)), 12, "agent",
            DropPosition: new(4.7, .5), DropTargetId: target, Power: new(100, 1), PickupPosition: chest.Position, PickupTargetId: chest.Id);
        map = map with { Entities = [.. map.Entities, chest, arm] };
        var plan = new FuelFeederPlanner().Find(map, "chest", "inserter", "boiler", 1);
        Assert.Equal(expectedArm, plan?.ExistingInserterId);
    }

    [Theory]
    [InlineData(9, 1, 0)]
    [InlineData(8, 1, 1)]
    [InlineData(8, 0, 2)]
    public void FuelHeldByTheArmIsNotYetReportedAsDelivered(long source, long inHand, long delivered)
    {
        Assert.Equal(delivered, new FeederStockReading(source, inHand).DeliveredSince(new(10, 0)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PlacesAChestAtNativePickupAndDropsInsideTheBoiler(double pickupDistance)
    {
        var map = Map(pickupDistance);
        var plan = new FuelFeederPlanner().Find(map, "chest", "inserter", "boiler", 1);
        Assert.NotNull(plan);
        Assert.Equal(plan.Container.Position, plan.Pickup);
        Assert.True(map.Entities.Single(e => e.Id == "boiler").Bounds.Contains(plan.Drop));
        Assert.Equal(pickupDistance, plan.Pickup.DistanceTo(plan.Inserter.Position), 8);
        Assert.True(new SpatialCollisionField(map).PlacementClear(map.Prototypes["inserter"], plan.Inserter.Position, plan.Inserter.Direction));
    }

    [Fact]
    public void DoesNotPretendAnUnpoweredInserterCanFeedTheBoiler()
    {
        Assert.Null(new FuelFeederPlanner().Find(Map(), "chest", "inserter", "boiler", 99));
    }

    [Fact]
    public void ExtendsPowerOnlyThroughAReachableNativeWireConnection()
    {
        var map = Map();
        var geometry = new Dictionary<string, EntityGeometry>(map.Prototypes)
        {
            ["pole"] = map.Prototypes["pole"] with { SupplyArea = 1, MaxWireDistance = 7.5 }
        };
        map = map with { Prototypes = geometry, Items = new Dictionary<string, PlaceableItem>(map.Items) { ["pole"] = new("pole", 50) } };
        var plan = new FuelFeederPlanner().Find(map, "chest", "inserter", "boiler", 1, "pole");
        Assert.NotNull(plan?.Pole);
        Assert.InRange(plan.Pole.Position.DistanceTo(map.Entities.Single(e => e.Id == "pole").Position), 0, 7.5);
    }

    internal static SpatialSnapshot Map(double pickupDistance = 1)
    {
        var map = SpatialPlannerTests.Map([]);
        var solid = map.Prototypes["wall"].Mask;
        var prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
        {
            ["inserter"] = new("inserter", "inserter", new(new(-.15, -.15), new(.15, .15)), solid, 1, 1,
                IsElectric: true, InserterPickup: new(0, -pickupDistance), InserterDrop: new(0, 1.2)),
            ["boiler"] = new("boiler", "boiler", new(new(-1.4, -.9), new(1.4, .9)), solid, 3, 2),
            ["pole"] = new("pole", "electric-pole", new(new(-.15, -.15), new(.15, .15)), solid, 1, 1, SupplyArea: 5)
        };
        return map with
        {
            Prototypes = prototypes,
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["inserter"] = new("inserter", 50) },
            Entities = [new("boiler", "boiler", new(5.5, 0), new(new(4.1, -.9), new(6.9, .9)), 0, "agent"),
                new("pole", "pole", new(2.5, 2.5), new(new(2.35, 2.35), new(2.65, 2.65)), 0, "agent", Power: new(0, 1))]
        };
    }
}
