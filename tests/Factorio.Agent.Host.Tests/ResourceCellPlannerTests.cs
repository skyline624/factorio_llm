using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ResourceCellPlannerTests
{
    private static readonly ResourceCellEquipment ElectricSmelter = new("electric-mining-drill", "iron-chest", "stone-furnace", "inserter", "small-electric-pole");
    private static readonly ResourceCellEquipment BurnerSmelter = ElectricSmelter with { Drill = "burner-mining-drill" };
    private static readonly ResourceCellEquipment ElectricMiner = new("electric-mining-drill", "iron-chest", Pole: "small-electric-pole");
    private static readonly ResourceCellEquipment BurnerMiner = new("burner-mining-drill", "iron-chest");

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void SmelterCellsChainDrillFurnaceInserterAndChestFromNativeVectors(int direction)
    {
        var map = FactoryMaps.Grass(30);
        var row = Row(map, ElectricSmelter, direction, cells: 2);
        var planner = new ResourceCellPlanner();
        var cells = Enumerable.Range(0, 2).Select(i => planner.Layout(map, row, i)).ToArray();
        foreach (var cell in cells)
        {
            var drill = cell.Role("drill")!;
            var furnace = cell.Role("furnace")!;
            var arm = cell.Role("output-inserter")!;
            var chest = cell.Role("output-chest")!;
            Assert.True(ExtractionPlanner.DropTile(DrillOutput(map, drill)).Overlaps(Box(map, furnace)), "The drill output misses the furnace.");
            Assert.True(Box(map, furnace).Contains(Offset(arm, map.Prototypes["inserter"].InserterPickup!)), "The inserter does not pick from the furnace.");
            Assert.Equal(ExtractionPlanner.DropTile(chest.Position), ExtractionPlanner.DropTile(Offset(arm, map.Prototypes["inserter"].InserterDrop!)));
            var pole = cell.Role("pole")!;
            Assert.True(PowerGridPlanner.Supplies(pole.Position, map.Prototypes["small-electric-pole"], Box(map, drill)));
            Assert.True(PowerGridPlanner.Supplies(pole.Position, map.Prototypes["small-electric-pole"], Box(map, arm)));
            // The chest opens onto the walkway so the actor services it without entering the cell.
            var chestTile = ExtractionPlanner.DropTile(chest.Position);
            Assert.True(new WorldBox(new(chestTile.Min.X - 1, chestTile.Min.Y - 1), new(chestTile.Max.X + 1, chestTile.Max.Y + 1)).Overlaps(cell.Walkway));
        }
        AssertSeparated(map, cells);
        Assert.Equal(row.Pitch, cells[0].Role("pole")!.Position.DistanceTo(cells[1].Role("pole")!.Position), 6);
        Assert.True(row.Pitch <= map.Prototypes["small-electric-pole"].MaxWireDistance);
    }

    [Fact]
    public void BurnerSmelterPolePowersOnlyTheInserterWithinTheDrillWidth()
    {
        var map = FactoryMaps.Grass(30);
        var row = Row(map, BurnerSmelter, 0, cells: 3);
        Assert.Equal(2, row.Pitch);
        var cells = Enumerable.Range(0, 3).Select(i => new ResourceCellPlanner().Layout(map, row, i)).ToArray();
        foreach (var cell in cells)
        {
            Assert.True(ExtractionPlanner.DropTile(DrillOutput(map, cell.Role("drill")!)).Overlaps(Box(map, cell.Role("furnace")!)));
            Assert.True(PowerGridPlanner.Supplies(cell.Role("pole")!.Position, map.Prototypes["small-electric-pole"], Box(map, cell.Role("output-inserter")!)));
        }
        AssertSeparated(map, cells);
    }

    [Fact]
    public void MinersDropStraightIntoTheirChestAndBurnerMinersNeedNoPole()
    {
        var map = FactoryMaps.Grass(30);
        var electric = new ResourceCellPlanner().Layout(map, Row(map, ElectricMiner, 8, cells: 1), 0);
        Assert.Equal(ExtractionPlanner.DropTile(electric.Role("output-chest")!.Position), ExtractionPlanner.DropTile(DrillOutput(map, electric.Role("drill")!)));
        Assert.True(PowerGridPlanner.Supplies(electric.Role("pole")!.Position, map.Prototypes["small-electric-pole"], Box(map, electric.Role("drill")!)));
        var burnerRow = Row(map, BurnerMiner, 0, cells: 2);
        var burner = new ResourceCellPlanner().Layout(map, burnerRow, 1);
        Assert.Null(burner.Role("pole"));
        Assert.Equal(2, burnerRow.Pitch);
        Assert.Equal(ExtractionPlanner.DropTile(burner.Role("output-chest")!.Position), ExtractionPlanner.DropTile(DrillOutput(map, burner.Role("drill")!)));
    }

    [Fact]
    public void RowMinesOnlyTheTargetOreAndKeepsAWalkwayBeyondTheChests()
    {
        var patch = FactoryMaps.Patch("iron-ore", -10, -6, 10, 6).Concat(FactoryMaps.Patch("coal", -10, 6, 10, 10)).ToList();
        patch.Add(new("tree-on-ore", "tree", new(0.5, 0.5), new(new(0.1, 0.1), new(0.9, 0.9)), 0, "neutral"));
        var map = FactoryMaps.Grass(40, patch);
        var catalog = Catalogs.Raw();
        var search = new ResourceCellPlanner().Find(map, catalog, ResourceCellPlanner.Supply(catalog, "iron-plate")!, ElectricSmelter, 3, new(0, 0));
        Assert.Equal(ResourceRowSearchStatus.Found, search.Status);
        var row = search.Row!;
        Assert.Equal(3, row.Cells);
        Assert.Equal(18.75, row.CellPerMinute, 6);
        var cells = Enumerable.Range(0, row.Cells).Select(i => new ResourceCellPlanner().Layout(map, row, i)).ToArray();
        double radius = map.Prototypes["electric-mining-drill"].MiningRadius!.Value;
        foreach (var drill in cells.Select(c => c.Role("drill")!))
        {
            var area = new WorldBox(new(drill.Position.X - radius, drill.Position.Y - radius), new(drill.Position.X + radius, drill.Position.Y + radius));
            var covered = map.Entities.Where(e => e.Amount > 0 && area.Overlaps(e.Bounds)).ToArray();
            Assert.NotEmpty(covered);
            Assert.All(covered, e => Assert.Equal("iron-ore", e.Name));
        }
        var walkway = ResourceCellPlanner.Walkway(map, row);
        Assert.DoesNotContain(cells.SelectMany(c => c.Entities), e => Box(map, e).Overlaps(walkway));
        var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != "tree-on-ore").ToArray() });
        for (double x = walkway.Min.X + .5; x < walkway.Max.X; x++)
            for (double y = walkway.Min.Y + .5; y < walkway.Max.Y; y++)
                Assert.True(field.Walkable(new(x, y)), $"Walkway tile {x},{y} is blocked.");
        // Only trees on reserved ground are reported for mining.
        var reserved = ResourceCellPlanner.Reservation(map, row);
        Assert.All(search.Clearance!, e => Assert.Contains(reserved, box => box.Overlaps(e.Bounds)));
        Assert.Equal(reserved.Any(box => box.Overlaps(patch[^1].Bounds)), search.Clearance!.Any(e => e.Id == "tree-on-ore"));
    }

    [Fact]
    public void InterleavedDepositsNeverShareAMiningArea()
    {
        // The checkerboard covers the whole observed window, so no mining area can reach a single-resource edge.
        var entities = new List<SpatialEntity>();
        for (int x = -16; x < 16; x++)
            for (int y = -16; y < 16; y++)
                entities.AddRange(FactoryMaps.Patch((x + y) % 2 == 0 ? "iron-ore" : "coal", x, y, x + 1, y + 1));
        var map = FactoryMaps.Grass(16, entities);
        var catalog = Catalogs.Raw();
        var search = new ResourceCellPlanner().Find(map, catalog, ResourceCellPlanner.Supply(catalog, "coal")!, BurnerMiner, 2, new(0, 0));
        Assert.Equal(ResourceRowSearchStatus.NoSite, search.Status);
        Assert.Null(search.Row);
    }

    [Fact]
    public void RowsWithoutAnExitBeyondTheirNeighbourhoodAreRejected()
    {
        // A 9x9 ore island holds one cell and its walkway, but the actor could never leave the serviced walkway.
        var map = FactoryMaps.Grass(30, FactoryMaps.Patch("iron-ore", -4, -4, 5, 5).ToArray(),
            (x, y) => x is >= -4 and < 5 && y is >= -4 and < 5 ? "grass" : "water");
        var catalog = Catalogs.Raw();
        var supply = ResourceCellPlanner.Supply(catalog, "iron-plate")!;
        Assert.Equal(ResourceRowSearchStatus.NoSite, new ResourceCellPlanner().Find(map, catalog, supply, ElectricSmelter, 2, new(0, 0)).Status);
        var open = FactoryMaps.Grass(30, FactoryMaps.Patch("iron-ore", -4, -4, 5, 5).ToArray());
        Assert.Equal(ResourceRowSearchStatus.Found, new ResourceCellPlanner().Find(open, catalog, supply, ElectricSmelter, 2, new(0, 0)).Status);
    }

    [Fact]
    public void BuildingsAndReservedRowsAreAvoidedAndBudgetExhaustionIsDistinct()
    {
        var box = new WorldBox(new(-1.19921875, -1.19921875), new(1.19921875, 1.19921875));
        var entities = FactoryMaps.Patch("iron-ore", -12, -8, 12, 8).Append(
            new SpatialEntity("machine", "assembling-machine-1", new(-8.5, 0.5), box.Translate(new(-8.5, 0.5)), 0, "agent")).ToArray();
        var reservedBox = new WorldBox(new(2, -8), new(12, 8));
        var map = ResourceCellPlanner.Reserve(FactoryMaps.Grass(40, entities), [reservedBox], "iron-chest");
        var catalog = Catalogs.Raw();
        var supply = ResourceCellPlanner.Supply(catalog, "iron-plate")!;
        var search = new ResourceCellPlanner().Find(map, catalog, supply, ElectricSmelter, 2, new(0, 0));
        Assert.Equal(ResourceRowSearchStatus.Found, search.Status);
        var planned = Enumerable.Range(0, search.Row!.Cells).SelectMany(i => new ResourceCellPlanner().Layout(map, search.Row, i).Entities);
        Assert.DoesNotContain(planned, e => Box(map, e).Overlaps(reservedBox) || Box(map, e).Overlaps(box.Translate(new(-8.5, 0.5))));
        Assert.Equal(ResourceRowSearchStatus.SearchBudgetExhausted,
            new ResourceCellPlanner().Find(map, catalog, supply, ElectricSmelter, 2, new(0, 0), maximumProofs: 0).Status);
        Assert.Equal(ResourceRowSearchStatus.NoSite,
            new ResourceCellPlanner().Find(FactoryMaps.Grass(40), catalog, supply, ElectricSmelter, 2, new(0, 0)).Status);
    }

    [Fact]
    public void PlannedCellsStopFittingWhenTheirGroundIsTaken()
    {
        var map = FactoryMaps.Grass(40, FactoryMaps.Patch("iron-ore", -12, -8, 12, 8).ToArray());
        var catalog = Catalogs.Raw();
        var planner = new ResourceCellPlanner();
        var row = planner.Find(map, catalog, ResourceCellPlanner.Supply(catalog, "iron-plate")!, ElectricSmelter, 2, new(0, 0)).Row!;
        var chest = planner.Layout(map, row, 1).Role("output-chest")!;
        var tile = ExtractionPlanner.DropTile(chest.Position);
        var blocked = map with { Entities = [.. map.Entities, new SpatialEntity("stranger", "wooden-chest", chest.Position,
            new(new(tile.Min.X + .15, tile.Min.Y + .15), new(tile.Max.X - .15, tile.Max.Y - .15)), 0, "agent")] };
        Assert.True(planner.Fits(blocked, catalog, row, 0));
        Assert.False(planner.Fits(blocked, catalog, row, 1));
        Assert.Throws<InvalidDataException>(() => planner.Layout(map, row with { Pitch = row.Pitch + 1 }, 0));
    }

    [Theory]
    [InlineData("iron-plate", "electric-mining-drill", 18.75)] // stone furnace: 60 / 3.2 s
    [InlineData("iron-plate", "burner-mining-drill", 15)]      // burner drill: 0.25 ore per second
    [InlineData("stone-brick", "electric-mining-drill", 15)]   // two stone per brick at 0.5 stone per second
    [InlineData("coal", "electric-mining-drill", 30)]
    [InlineData("coal", "burner-mining-drill", 15)]
    public void CellRatesComeFromDrillFurnaceAndInserterLimits(string product, string drill, double perMinute)
    {
        var map = FactoryMaps.Grass(10);
        var catalog = Catalogs.Raw();
        var supply = ResourceCellPlanner.Supply(catalog, product)!;
        var equipment = supply.Kind == "smelter" ? ElectricSmelter with { Drill = drill } : ElectricMiner with { Drill = drill };
        Assert.Equal(perMinute, ResourceCellPlanner.CellPerMinute(map, catalog, supply, equipment), 6);
    }

    [Fact]
    public void SupplyChoosesMinersForDepositsAndSmeltersForSingleOreRecipes()
    {
        var catalog = Catalogs.Raw();
        Assert.Equal(new ResourceSupply("miner", "coal", "coal"), ResourceCellPlanner.Supply(catalog, "coal"));
        var iron = ResourceCellPlanner.Supply(catalog, "iron-plate")!;
        Assert.Equal(("smelter", "iron-ore", "iron-plate"), (iron.Kind, iron.Resource, iron.Recipe!.Name));
        Assert.Equal("stone", ResourceCellPlanner.Supply(catalog, "stone-brick")!.Resource);
        Assert.Null(ResourceCellPlanner.Supply(catalog, "steel-plate"));
        Assert.Null(ResourceCellPlanner.Supply(catalog, "iron-gear-wheel"));
        Assert.Null(ResourceCellPlanner.Supply(catalog, "wood"));
    }

    [Fact]
    public void EquipmentPrefersElectricDrillsAndFallsBackToBurnerMiners()
    {
        var map = FactoryMaps.Grass(10);
        var catalog = Catalogs.Raw();
        var none = new Dictionary<string, long>();
        Assert.Equal(ElectricSmelter, ResourceCellPlanner.Equipment(catalog, map, ResourceCellPlanner.Supply(catalog, "iron-plate")!, none));
        var early = Catalogs.Raw(electricDrill: false);
        Assert.Equal(BurnerSmelter, ResourceCellPlanner.Equipment(early, map, ResourceCellPlanner.Supply(early, "iron-plate")!, none));
        // A carried drill is usable even before its recipe is researched.
        Assert.Equal(ElectricSmelter, ResourceCellPlanner.Equipment(early, map, ResourceCellPlanner.Supply(early, "iron-plate")!,
            new Dictionary<string, long> { ["electric-mining-drill"] = 1 }));
        var unpowered = early with { Recipes = early.Recipes.Select(r => r.Name is "inserter" or "small-electric-pole" ? r with { Enabled = false } : r).ToArray() };
        Assert.Equal(BurnerMiner, ResourceCellPlanner.Equipment(unpowered, map, ResourceCellPlanner.Supply(unpowered, "coal")!, none));
        Assert.Null(ResourceCellPlanner.Equipment(unpowered, map, ResourceCellPlanner.Supply(unpowered, "iron-plate")!, none));
    }

    private static ResourceRow Row(SpatialSnapshot map, ResourceCellEquipment equipment, int direction, int cells) =>
        new(1, equipment.Furnace is null ? "miner" : "smelter", "product", "ore", equipment, new(0, 0), direction,
            ResourceCellPlanner.Pitch(map, equipment, direction) ?? throw new InvalidDataException("No template."), cells, 1);

    private static WorldBox Box(SpatialSnapshot map, PlannedEntity e) =>
        map.Prototypes[map.Items[e.Item].EntityName].CollisionBox.Rotate(e.Direction).Translate(e.Position);

    private static MapPosition Offset(PlannedEntity e, MapPosition vector)
    {
        var rotated = ExtractionPlanner.Rotate(vector, e.Direction);
        return new(e.Position.X + rotated.X, e.Position.Y + rotated.Y);
    }

    private static MapPosition DrillOutput(SpatialSnapshot map, PlannedEntity drill)
    {
        var rotated = ExtractionPlanner.Rotate(map.Prototypes[drill.Item].MiningOutput!, drill.Direction);
        return new(drill.Position.X + Math.Truncate(rotated.X * 256) / 256, drill.Position.Y + Math.Truncate(rotated.Y * 256) / 256);
    }

    private static void AssertSeparated(SpatialSnapshot map, IReadOnlyList<CellLayout> cells)
    {
        var entities = cells.SelectMany(c => c.Entities).ToArray();
        var boxes = entities.Select(e => Box(map, e)).ToArray();
        for (int a = 0; a < boxes.Length; a++)
            for (int b = a + 1; b < boxes.Length; b++)
                Assert.False(boxes[a].Overlaps(boxes[b]), $"{entities[a]} overlaps {entities[b]}");
        var field = new SpatialCollisionField(map);
        foreach (var e in entities)
            Assert.True(field.PlacementClear(map.Prototypes[map.Items[e.Item].EntityName], e.Position, e.Direction));
        foreach (var cell in cells)
            Assert.DoesNotContain(boxes, box => box.Overlaps(cell.Walkway));
    }
}
