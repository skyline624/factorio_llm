using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryLogisticsTests
{
    private static readonly ActorScope Scope = new("world", "session", "actor", 1, 1);

    [Fact]
    public void CellFurnacesBurnerDrillsAndBoilersAreRefuelledButElectricDrillsAndStrangersAreNot()
    {
        var snapshot = new FactorySnapshot("snapshot", Scope, 100, 200, Protocol.ToElement(new { }), [
            Entity("furnace", "furnace", fuel: "inventory:furnace:1"), Inventory("inventory:furnace:1", "furnace", new { coal = 2 }),
            Entity("burner-drill", "mining-drill", fuel: "inventory:burner-drill:1"), Inventory("inventory:burner-drill:1", "burner-drill", new { }),
            Entity("electric-drill", "mining-drill"),
            Entity("stranger", "furnace", fuel: "inventory:stranger:1"), Inventory("inventory:stranger:1", "stranger", new { }),
            Entity("boiler", "boiler", fuel: "inventory:boiler:1"), Inventory("inventory:boiler:1", "boiler", new { coal = 30 })]);
        FactoryCell[] cells =
        [
            Cell("smelter", "ready", new() { ["drill"] = "burner-drill", ["furnace"] = "furnace", ["output-chest"] = "chest-1" }),
            Cell("miner", "ready", new() { ["drill"] = "electric-drill", ["output-chest"] = "chest-2" })
        ];
        var burners = FactoryLogistics.Burners(snapshot, cells);
        // Only the boiler's starvation stops the electric network, which is survival rather than production.
        Assert.Equal([("boiler", 30L, true), ("burner-drill", 0L, false), ("furnace", 2L, false)],
            burners.Select(b => (b.EntityId, b.Loaded, b.PowerSource)).ToArray());
    }

    [Fact]
    public void ScarceFuelBringsEveryStarvedBurnerToAQuarterStackBeforeToppingAnyUp()
    {
        // A quarter of a 50-coal stack is 12: the loaded boiler is skipped, both starved burners start.
        Assert.Equal([20L, 10L, 0L], FactoryLogistics.PlanFuel([0, 2, 30], available: 30, stack: 50));
        Assert.Equal([50L, 48L], FactoryLogistics.PlanFuel([0, 2], available: 200, stack: 50));
        Assert.Equal([12L, 3L], FactoryLogistics.PlanFuel([0, 0], available: 15, stack: 50));
        Assert.Equal([0L], FactoryLogistics.PlanFuel([0], available: 0, stack: 50));
    }

    [Fact]
    public void ScarceIngredientsBalancePlannedBufferFractionsBeforeFillingAnyChest()
    {
        // Campaign 2026-09-30 (seed 20261002): chests were refilled in registry order, so the first gear consumers took every
        // gear and the inserter cell listed after them stayed empty for two hours.
        Assert.Equal([10L, 10L, 10L], FactoryLogistics.PlanShares([(0, 40), (0, 40), (0, 40)], available: 30));
        Assert.Equal([20L, 20L, 20L], FactoryLogistics.PlanShares([(0, 40), (0, 40), (0, 40)], available: 60));
        // A half-full chest waits while the less full buffers catch up.
        Assert.Equal([0L, 10L, 5L], FactoryLogistics.PlanShares([(20, 40), (0, 40), (5, 40)], available: 15));
        Assert.Equal([0L, 0L], FactoryLogistics.PlanShares([(40, 40), (0, 40)], available: 0));
    }

    [Fact]
    public void GreenScienceGearSharesFollowTheTwoBeltsPerCraftYield()
    {
        // Four packs/min require two belt crafts and four inserter crafts/min. With 25 gears, FIFO gave 15/10 and
        // could make only 10 packs; 8/17 gives 16 belts and 17 inserters, enough for 16 native green packs.
        Assert.Equal([8L, 17L], FactoryLogistics.PlanShares([(0, 20), (0, 40)], available: 25));
        Assert.Equal([17L, 8L], FactoryLogistics.PlanShares([(0, 40), (0, 20)], available: 25));
        Assert.Equal([10L, 0L], FactoryLogistics.PlanShares([(0, 20), (20, 40)], available: 10));
        Assert.Equal([20L, 40L], FactoryLogistics.PlanShares([(0, 20), (0, 40)], available: 100));
    }

    [Fact]
    public void PlannedCellsBufferTenMinutesOfTheirShareAndUnplannedOnesTheMinimum()
    {
        // Campaign 2026-09-30 (seed 20261002): a magazine cell nobody planned kept 160 iron plates in its chest while science
        // cells starved; flat 40-craft buffers ignore what each cell actually has to deliver.
        var shares = new Dictionary<string, double> { ["automation-science-pack"] = 3, ["iron-gear-wheel"] = 10, ["copper-cable"] = 0.2 };
        Assert.Equal(30, FactoryLogistics.BufferCrafts(shares, "automation-science-pack", maximum: 40));
        Assert.Equal(40, FactoryLogistics.BufferCrafts(shares, "iron-gear-wheel", 40));
        Assert.Equal(FactoryLogistics.MinimumBufferCrafts, FactoryLogistics.BufferCrafts(shares, "copper-cable", 40));
        Assert.Equal(FactoryLogistics.MinimumBufferCrafts, FactoryLogistics.BufferCrafts(shares, "firearm-magazine", 40));
        // Without any registered target (prepared fixtures, older registries) the caller's buffer applies unchanged.
        Assert.Equal(50, FactoryLogistics.BufferCrafts(null, "iron-gear-wheel", 50));
    }

    [Fact]
    public void CellSharesSplitEachPlannedStageAcrossItsReadyCells()
    {
        var state = new FactoryState(1, "world", [], [
            Assembler("red-1", "automation-science-pack"), Assembler("red-2", "automation-science-pack"), Assembler("gears", "iron-gear-wheel")])
            .WithTarget("automation-science-pack", 12);
        var shares = FactoryLogistics.CellShares(Catalogs.Early(), state)!;
        Assert.Equal(6, shares["automation-science-pack"], 6);
        Assert.Equal(12, shares["iron-gear-wheel"], 6);
        Assert.Null(FactoryLogistics.CellShares(Catalogs.Early(), state with { Targets = null }));
    }

    [Fact]
    public void PermanentInputLinksShareProducerPausePolicyAndExcludeCommittedScience()
    {
        var catalog = Catalogs.Early();
        FactoryCell[] cells = [Assembler("red", "automation-science-pack"), Assembler("gears", "iron-gear-wheel")];
        var caps = new Dictionary<string, long> { ["automation-science-pack"] = 240, ["iron-gear-wheel"] = 120 };
        var snapshot = new FactorySnapshot("s", Scope, 100, 200, Protocol.ToElement(new { }),
            [new("lab", "inventory", "lab", "lab_input", Protocol.ToElement(new { items = new Dictionary<string, long> { ["automation-science-pack"] = 200 } })),
             new("bag", "inventory", "actor", "character_main", Protocol.ToElement(new { items = new Dictionary<string, long>
                { ["automation-science-pack"] = 54, ["iron-gear-wheel"] = 3 } }))]);
        var available = FactoryLogistics.AvailableStock(snapshot);
        Assert.Equal(54, available["automation-science-pack"]);
        Assert.Empty(FactoryLogistics.PausedCells(cells, catalog, caps, available));
        available["automation-science-pack"] = 240;
        Assert.Equal("red", Assert.Single(FactoryLogistics.PausedCells(cells, catalog, caps, available)).Cell.Id);
        Assert.Empty(FactoryLogistics.PausedCells(cells, catalog, null, available));
    }

    [Fact]
    public void ProducersPauseOnceTheirProductHoldsTwentyMinutesOfItsPlannedRate()
    {
        // Campaign 2026-09-30 (seed 20261002): unregulated cells piled up 1338 red packs, 509 magazines and 445 belts, about
        // 2500 iron plates, while inserters and gears for green science stayed short.
        var state = new FactoryState(1, "world", [], [Assembler("red", "automation-science-pack"), Assembler("gears", "iron-gear-wheel")])
            .WithTarget("automation-science-pack", 8);
        var caps = FactoryLogistics.StockCaps(Catalogs.Early(), state)!;
        Assert.Equal(160, caps["automation-science-pack"]);
        Assert.Equal(160, caps["iron-gear-wheel"]);
        Assert.True(FactoryLogistics.Paused(caps, "automation-science-pack", stock: 1338));
        Assert.False(FactoryLogistics.Paused(caps, "iron-gear-wheel", stock: 21));
        Assert.True(FactoryLogistics.Paused(caps, "firearm-magazine", stock: FactoryLogistics.UnplannedStock));
        Assert.False(FactoryLogistics.Paused(caps, "firearm-magazine", stock: FactoryLogistics.UnplannedStock - 1));
        // Without registered targets nothing pauses, as in prepared fixtures.
        Assert.Null(FactoryLogistics.StockCaps(Catalogs.Early(), state with { Targets = null }));
        Assert.False(FactoryLogistics.Paused(null, "automation-science-pack", stock: 100000));
    }

    [Fact]
    public void CollectionKeepsTwoStacksBeyondWhatChestsAndLabsNeed()
    {
        // Campaign 2026-10-01 (seed 20261002): every output chest was emptied into the bag each round; after a corpse recovery
        // returned 4286 iron plates the full bag refused later takes with transfer_blocked.
        Assert.Equal(330, FactoryLogistics.CollectCap(need: 130, stackSize: 100));
        Assert.Equal(200, FactoryLogistics.CollectCap(need: 0, stackSize: 100));
        var caps = new Dictionary<string, long> { ["iron-plate"] = 330, ["copper-cable"] = 400 };
        var surplus = FactoryLogistics.Surplus(new Dictionary<string, long> { ["iron-plate"] = 4286, ["copper-cable"] = 437, ["wood"] = 3 },
            item => caps.GetValueOrDefault(item, 200), item => item == "copper-cable" ? 200 : 100);
        // Largest surplus in stacks first; items within their cap stay carried.
        Assert.Equal([("iron-plate", 3956L)], surplus.Where(s => s.Item == "iron-plate").ToArray());
        Assert.Equal(["iron-plate", "copper-cable"], surplus.Select(s => s.Item));
        Assert.DoesNotContain(surplus, s => s.Item == "wood");
    }

    [Fact]
    public void DemandPullCountsChestsOutputsAndTheBagButNotCorpsesOrLoadedTurrets()
    {
        // Campaign 2026-10-01 (seed 20261002): 476 magazines on corpses kept the magazine cell paused with none in any chest.
        static FactoryRecord Holding(string id, string owner, string name, int magazines) =>
            new(id, "inventory", owner, name, Protocol.ToElement(new { items = new Dictionary<string, long> { ["firearm-magazine"] = magazines } }));
        var snapshot = new FactorySnapshot("snapshot", Scope, 100, 200, Protocol.ToElement(new { }), [
            Holding("corpse-inventory", "corpse:1:2:1", "character_corpse", 323),
            Holding("turret-ammo", "turret", "turret_ammo", 11),
            Holding("assembler-input", "assembler", "crafter_input", 4),
            Holding("output-chest", "chest", "chest", 7),
            Holding("assembler-output", "assembler", "crafter_output", 2),
            Holding("bag", "actor", "character_main", 5)]);
        Assert.Equal(14, FactoryLogistics.AvailableStock(snapshot)["firearm-magazine"]);
        Assert.False(FactoryLogistics.Paused(new Dictionary<string, long>(), "firearm-magazine", 14));
    }

    [Fact]
    public void AnEmptyBagEncodedAsAnEmptyObjectLeavesEveryUsableSlotFree()
    {
        // Campaign 2026-10-01 (seed 20261002): after a death the respawned actor's empty "stacks" list arrived as {} and
        // every maintenance round between goals failed on it.
        static FactorySnapshot Bag(object stacks) => new("snapshot", Scope, 100, 200, Protocol.ToElement(new { }), [
            new("actor", "entity", "actor", "character", Protocol.ToElement(new { role = "actor", type = "character", mainInventoryId = "main" })),
            new("main", "inventory", "actor", "main", Protocol.ToElement(new { role = "actor", items = new { }, usableSlots = 80, stacks }))]);
        Assert.Equal(80, FactoryLogistics.FreeSlots(Bag(new { })));
        Assert.Equal(78, FactoryLogistics.FreeSlots(Bag(new object[] { new { name = "coal", count = 50 }, new { name = "wood", count = 3 } })));
    }

    private static FactoryCell Assembler(string id, string recipe) => new(id, 1, new(0, 0, true), "assembler", "assembling-machine-1", recipe,
        new Dictionary<string, string> { ["machine"] = id, ["input-chest"] = id + "-in", ["output-chest"] = id + "-out" }, "ready", 1);

    [Fact]
    public void TheCoalMinerIsFuelledFirstThenPowerThenTheRest()
    {
        // Campaign 2026-09-30 (seed 20261002): the new coal miner never got its own coal, so nothing refilled the others.
        FactoryCell[] cells =
        [
            new("coal", 0, new(1, 0, true), "miner", "burner-mining-drill", FactoryLogistics.Fuel,
                new Dictionary<string, string> { ["drill"] = "z-coal-drill", ["output-chest"] = "c" }, "ready", 1),
            new("iron", 0, new(2, 0, true), "smelter", "burner-mining-drill", "iron-plate",
                new Dictionary<string, string> { ["drill"] = "a-iron-drill", ["furnace"] = "b-furnace", ["output-chest"] = "p" }, "ready", 1)
        ];
        var ordered = FactoryLogistics.FuelOrder([("a-iron-drill", 0, false), ("b-furnace", 0, false), ("boiler", 0, true), ("z-coal-drill", 0, false)], cells);
        Assert.Equal(["z-coal-drill", "boiler", "a-iron-drill", "b-furnace"], ordered.Select(b => b.EntityId));
    }

    [Theory]
    [InlineData(0, 0, 12)]
    [InlineData(5, 0, 7)]
    [InlineData(0, 12, 0)]
    [InlineData(20, 0, 0)]
    public void AStarvedBurnerIsShortOnlyOfWhatRestartsIt(long loaded, long given, long expected) =>
        // Reporting full stacks made the actor mine 250 coal by hand-fed drills before any cell could deliver.
        Assert.Equal(expected, FactoryLogistics.FuelShortfall(loaded, given, stack: 50));

    [Fact]
    public void OutputChestsOfEveryRegisteredCellAreCollectedWhateverItsStatus()
    {
        // Campaign 2026-10-01 (seed 20261002): plates in the chests of reopened and depleted iron cells were never collected.
        FactoryCell[] cells =
        [
            Cell("smelter", "ready", new() { ["furnace"] = "f", ["output-chest"] = "plates" }),
            Cell("miner", "ready", new() { ["drill"] = "d", ["output-chest"] = "coal" }),
            Cell("assembler", "ready", new() { ["machine"] = "m", ["input-chest"] = "in", ["output-chest"] = "gears" }),
            Cell("smelter", "building", new() { ["output-chest"] = "reopened" }),
            Cell("smelter", ResourceCellHealth.Depleted, new() { ["drill"] = "dd", ["output-chest"] = "exhausted" }),
            Cell("lab", "ready", new() { ["machine"] = "lab" })
        ];
        Assert.Equal(["plates", "coal", "gears", "reopened", "exhausted"], FactoryLogistics.OutputChests(cells));
    }

    private static FactoryCell Cell(string kind, string status, Dictionary<string, string> entities) =>
        new($"cell-{kind}-{entities.Count}", 0, new(1, 0, true), kind, "machine", null, entities, status, 1);

    private static FactoryRecord Entity(string id, string type, string? fuel = null) => new(id, "entity", id, type,
        fuel is null ? Protocol.ToElement(new { role = "factory", type }) : Protocol.ToElement(new { role = "factory", type, fuelInventoryId = fuel }));

    private static FactoryRecord Inventory(string id, string owner, object items) =>
        new(id, "inventory", owner, "fuel", Protocol.ToElement(new { role = "factory", items }));
}
