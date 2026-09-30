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
        Assert.Equal([("boiler", 30L), ("burner-drill", 0L), ("furnace", 2L)], burners.Select(b => (b.EntityId, b.Loaded)).ToArray());
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
