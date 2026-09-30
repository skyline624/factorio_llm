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
    public void OutputChestsOfEveryReadyCellAreCollected()
    {
        FactoryCell[] cells =
        [
            Cell("smelter", "ready", new() { ["furnace"] = "f", ["output-chest"] = "plates" }),
            Cell("miner", "ready", new() { ["drill"] = "d", ["output-chest"] = "coal" }),
            Cell("assembler", "ready", new() { ["machine"] = "m", ["input-chest"] = "in", ["output-chest"] = "gears" }),
            Cell("miner", "building", new() { ["output-chest"] = "unfinished" }),
            Cell("lab", "ready", new() { ["machine"] = "lab" })
        ];
        Assert.Equal(["plates", "coal", "gears"], FactoryLogistics.OutputChests(cells));
    }

    private static FactoryCell Cell(string kind, string status, Dictionary<string, string> entities) =>
        new($"cell-{kind}-{entities.Count}", 0, new(1, 0, true), kind, "machine", null, entities, status, 1);

    private static FactoryRecord Entity(string id, string type, string? fuel = null) => new(id, "entity", id, type,
        fuel is null ? Protocol.ToElement(new { role = "factory", type }) : Protocol.ToElement(new { role = "factory", type, fuelInventoryId = fuel }));

    private static FactoryRecord Inventory(string id, string owner, object items) =>
        new(id, "inventory", owner, "fuel", Protocol.ToElement(new { role = "factory", items }));
}
