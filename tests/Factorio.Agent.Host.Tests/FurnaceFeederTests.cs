using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FurnaceFeederTests
{
    [Fact]
    public void OnlyFuelBurningFurnacesWithoutAFeederArePending()
    {
        // On 2026-10-08, 10 to 24 furnaces per campaign stood without fuel between the actor's hand deliveries.
        var burning = Cell("iron", "smelter", 0, ["furnace", "output-chest"]);
        var electric = Cell("steel", "furnace-cell", 2, ["furnace", "output-chest"]);
        var fed = Cell("copper", "smelter", 0, ["furnace", "output-chest"]);
        var feeder = Feeder(fed);
        var state = State(burning, electric, fed, feeder);
        var snapshot = Snapshot("iron:furnace", "copper:furnace");
        Assert.Equal(["iron"], FurnaceFeederController.Pending(state, snapshot).Select(c => c.Id));
    }

    [Fact]
    public void AFurnaceIsBeltFuelledOnlyWhileAHealthyBusCoversItsFeederWithCoal()
    {
        var furnace = Cell("iron", "smelter", 0, ["furnace", "output-chest"]);
        var feeder = Feeder(furnace);
        var state = State(furnace, feeder);
        Assert.Empty(FurnaceFeederController.BeltFuelled(state, new HashSet<(string, string)>()));
        Assert.Empty(FurnaceFeederController.BeltFuelled(state, new HashSet<(string, string)> { (feeder.Entities["input-chest"], "iron-ore") }));
        Assert.Equal(["iron:furnace"], FurnaceFeederController.BeltFuelled(state,
            new HashSet<(string, string)> { (feeder.Entities["input-chest"], FactoryLogistics.Fuel) }));
    }

    [Fact]
    public void AFeederTakesOnlyCoalFromBusesAtAFurnaceRate()
    {
        var catalog = Catalogs.Early();
        var furnace = Cell("iron", "smelter", 0, ["furnace", "output-chest"]);
        var feeder = Feeder(furnace);
        var recipe = FactoryTransportBuilder.InputRecipe(catalog, State(furnace, feeder), feeder)!;
        Assert.Equal([FactoryLogistics.Fuel], recipe.Ingredients.Select(i => i.Name));
        Assert.All(recipe.Ingredients, i => Assert.True(i.DeterministicItem));
        Assert.Equal(FactoryTransportBuilder.FurnaceFuelPerMinute, FactoryTransportBuilder.InputVolume(catalog, feeder, recipe.Ingredients[0]));
        Assert.Equal(FactoryTransportBuilder.FurnaceFuelPerMinute, FactoryTransportCoverage.Demand(catalog, null, feeder, FactoryLogistics.Fuel, null));
        Assert.Null(FactoryTransportCoverage.Demand(catalog, null, feeder, "iron-ore", null));
    }

    [Fact]
    public void PlansAFeederIntoAFurnaceFuelSlotLikeABoiler()
    {
        var map = FuelFeederPlannerTests.Map();
        map = map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { ["boiler"] = map.Prototypes["boiler"] with { Type = "furnace" } }
        };
        var plan = new FuelFeederPlanner().Find(map, "chest", "inserter", "boiler", 1);
        Assert.NotNull(plan);
        Assert.True(map.Entities.Single(e => e.Id == "boiler").Bounds.Contains(plan.Drop));
    }

    private static FactoryCell Feeder(FactoryCell furnace) => new(FurnaceFeederController.FeederId(furnace), 0, new(0, 0, true),
        FurnaceFeederController.Kind, "wooden-chest", null, new Dictionary<string, string> { ["input-chest"] = $"{furnace.Id}:feed-chest",
            ["input-inserter"] = $"{furnace.Id}:feed-arm" }, "ready", 1);

    private static FactoryCell Cell(string id, string kind, int zone, string[] roles) =>
        new(id, zone, new(0, 0, true), kind, "stone-furnace", id + "-plate", roles.ToDictionary(r => r, r => $"{id}:{r}"), "ready", 1);

    private static FactoryState State(params FactoryCell[] cells) => new(1, "w", [new(1, new(0, 0), 4, 6, 6)], cells);

    private static FactorySnapshot Snapshot(params string[] burningFurnaces) => new("s", new("world", "session", "actor", 1, 1), 1, 1,
        JsonSerializer.SerializeToElement(new { atomic = true }), burningFurnaces.Select(f => new FactoryRecord($"inventory:{f}:1", "inventory", f,
            "fuel", JsonSerializer.SerializeToElement(new { items = new { } }))).ToArray());
}
