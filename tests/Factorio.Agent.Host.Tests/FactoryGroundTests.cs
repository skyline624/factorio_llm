using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryGroundTests
{
    private static readonly CellEquipment Chemical = new("chemical-plant", "inserter", "iron-chest", "small-electric-pole");

    [Fact]
    public void AnUnbuiltBandSlotKeepsAFluidCellAndItsPipesOff()
    {
        var (map, stock) = OilMaps.WithGasSource([]);
        FluidSupplyRoute? Route(SpatialSnapshot current, string fluid) =>
            new FluidSupplyPlanner().Find(current, stock, "pipe", FluidCellPlanner.PlannedId, fluid);
        FluidCellSite? Find(SpatialSnapshot planning) =>
            new FluidCellPlanner().Find(planning, "agent", Chemical, "pipe", new(-10, 0), true, true, ["petroleum-gas"], Route);
        // A band planned exactly where the unreserved search would put the chemical plant.
        var free = Find(map)!;
        var body = Box(map, free.Layout.Machine);
        var zone = new FactoryZone(1, new(Math.Floor(body.Min.X), Math.Floor(body.Min.Y)), 1, 3, 3);
        var state = new FactoryState(1, "world", [zone], []);

        var reserved = FactoryGround.Reserve(map, new FactoryGround(state, null).Boxes(map), "pipe");
        var site = Find(reserved)!;
        Assert.All(site.Layout.Entities, e => Assert.False(Box(map, e).Overlaps(zone.Box), $"{e.Role} stands on the band."));
        Assert.All(site.Supplies.SelectMany(s => s.Supply.Route.Pipes), p => Assert.False(zone.Box.Contains(p), $"A pipe at {p} crosses the band."));
    }

    [Fact]
    public void AnExtractorLeavesADepositUnderAnUnbuiltBandForAFreeOne()
    {
        var map = OilMaps.Map([OilMaps.Crude("near", new(4.5, .5), 300000), OilMaps.Crude("far", new(16.5, .5), 300000)]);
        var pumpjack = Chemical with { Machine = "pumpjack" };
        Assert.Equal("near", new FluidCellPlanner().FindExtractor(map, pumpjack, "crude-oil", "pipe")!.ResourceId);
        // The band's box (0, -4)-(9, 5) covers the nearer deposit and every tile around it.
        var ground = new FactoryGround(new FactoryState(1, "world", [new FactoryZone(1, new(0, -4), 1, 9, 9)], []), null);
        var site = new FluidCellPlanner().FindExtractor(FactoryGround.Reserve(map, ground.Boxes(map), "pipe"), pumpjack, "crude-oil", "pipe")!;
        Assert.Equal("far", site.ResourceId);
        Assert.All(site.Layout.Entities, e => Assert.False(Box(map, e).Overlaps(ground.State.Zones[0].Box), $"{e.Role} stands on the band."));
    }

    [Fact]
    public void BandsAndResourceRowsAreReservedWithTheirUnbuiltCells()
    {
        var map = FactoryMaps.Grass(30);
        var equipment = new ResourceCellEquipment("electric-mining-drill", "iron-chest", "stone-furnace", "inserter", "small-electric-pole");
        var row = new ResourceRow(1, "smelter", "iron-plate", "iron-ore", equipment, new(10, 10), 0,
            ResourceCellPlanner.Pitch(map, equipment, 0) ?? throw new InvalidDataException("No template."), 3, 18.75);
        var zone = new FactoryZone(1, new(-20, -20), 8, 4, 9);
        var state = new FactoryState(1, "world", [zone], [], [row]);
        var boxes = new FactoryGround(state, null).Boxes(map);
        Assert.Contains(zone.Box, boxes);
        Assert.All(ResourceCellPlanner.Reservation(map, row), b => Assert.Contains(b, boxes));
        Assert.Equal(["electric-mining-drill", "inserter", "iron-chest", "small-electric-pole", "stone-furnace"],
            new FactoryGround(state, null).Items.Distinct().Order());
        // The character still walks over reserved ground; only buildings are kept off it.
        var reserved = FactoryGround.Reserve(map, boxes, "small-electric-pole");
        Assert.True(new SpatialCollisionField(reserved).PlacementClear(map.Prototypes["character"], new(-18, -18), 0));
        Assert.False(new SpatialCollisionField(reserved).PlacementClear(map.Prototypes["small-electric-pole"], new(-18.5, -18.5), 0));
    }

    private static WorldBox Box(SpatialSnapshot map, PlannedEntity e) =>
        map.Prototypes[map.Items[e.Item].EntityName].CollisionBox.Rotate(e.Direction).Translate(e.Position);
}
