using Factorio.Agent.Core;
using Factorio.Agent.Ollama;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SiloCellTests
{
    private static readonly ActorScope Scope = new("world", "session", "actor", 1, 1);
    private static readonly HashSet<string> Machines = new(StringComparer.Ordinal) { "assembling-machine-2", "stone-furnace", "rocket-silo" };

    [Fact]
    public void RocketPartsBecomeTheSiloStageAndFluidFedIngredientsStayRaw()
    {
        var plan = AutomationPlanner.Plan(SiloCatalogs.Rocket(), "rocket-part", 1, Machines);
        var silo = plan.Stages.Single(s => s.Recipe == "rocket-part");
        Assert.Equal((SiloCellPlanner.Kind, "rocket-silo", "rocket-part", 1), (silo.Kind, silo.MachineItem, silo.Item, silo.Machines));
        Assert.Equal(1, silo.CraftsPerMinute, 6);
        // recipe.lua: structures are crafted from solids behind the silo; processing units and rocket fuel need fluid-fed assemblers.
        Assert.Equal(["low-density-structure", "rocket-part", "steel-plate"], plan.Stages.Select(s => s.Recipe));
        Assert.Equal(("assembler", 10.0), (plan.Stages[0].Kind, plan.Stages[0].CraftsPerMinute));
        Assert.Equal(FurnaceCellPlanner.Kind, plan.Stages[2].Kind);
        Assert.Equal(10, plan.RawPerMinute["processing-unit"], 6);
        Assert.Equal(10, plan.RawPerMinute["rocket-fuel"], 6);
        Assert.Equal(200, plan.RawPerMinute["copper-plate"], 6);
        Assert.Equal(50, plan.RawPerMinute["plastic-bar"], 6);
        Assert.Equal(100, plan.RawPerMinute["iron-plate"], 6);
    }

    [Fact]
    public void OneArmBoundsTheSiloCellAndOneSiloServesTheFactory()
    {
        var catalog = SiloCatalogs.Rocket();
        var recipe = catalog.Recipes.Single(r => r.Name == "rocket-part");
        // Thirty ingredients per part through one 0.8 item/s inserter: 1.6 parts a minute, far below the silo's native 20.
        Assert.Equal(1.6, AutomationPlanner.CellCraftsPerMinute(catalog, recipe, "rocket-silo"), 6);
        var stage = AutomationPlanner.Plan(catalog, "rocket-part", 5, Machines).Stages.Single(s => s.Kind == SiloCellPlanner.Kind);
        Assert.Equal(1, stage.Machines);
        Assert.Equal(1, AutomationPlanner.MissingMachines(catalog, stage, []));
        Assert.Equal(0, AutomationPlanner.MissingMachines(catalog, stage, ["rocket-silo"]));
    }

    [Fact]
    public void OnlyAnEnabledSiloJoinsTheMachinesCellsAreBuiltWith()
    {
        Assert.Contains("rocket-silo", FactoryDirector.MachineItems(SiloCatalogs.Rocket()));
        var locked = SiloCatalogs.Rocket(siloEnabled: false);
        Assert.DoesNotContain("rocket-silo", FactoryDirector.MachineItems(locked));
        var plan = AutomationPlanner.Plan(locked, "rocket-part", 1, Machines);
        Assert.DoesNotContain(plan.Stages, s => s.Kind == SiloCellPlanner.Kind);
        Assert.Equal(1, plan.RawPerMinute["rocket-part"], 6);
    }

    [Fact]
    public void TheCatalogCarriesNativeSiloPrototypesAndRejectsInconsistentOnes()
    {
        var catalog = SiloCatalogs.Rocket();
        Assert.Equal(SiloCatalogs.Silo, ProductionCatalog.Parse(Response(catalog)).Silos!["rocket-silo"]);
        var idle = catalog with { Silos = new Dictionary<string, RocketSiloPrototype> { ["rocket-silo"] = SiloCatalogs.Silo with { CraftingSpeed = 0 } } };
        Assert.Throws<InvalidDataException>(() => ProductionCatalog.Parse(Response(idle)));
        var partless = catalog with { Silos = new Dictionary<string, RocketSiloPrototype> { ["rocket-silo"] = SiloCatalogs.Silo with { PartsRequired = 0 } } };
        Assert.Throws<InvalidDataException>(() => ProductionCatalog.Parse(Response(partless)));
    }

    [Fact]
    public void RocketPartAutomationIsGroundedOnceTheSiloIsResearched()
    {
        var goal = new GoalProposal("observation", "Feed a silo", GoalCategory.Production, "rocket-part", 1, GoalUnit.ItemsPerMinute,
            GoalPriority.Normal, new(TimeSpan.Zero, 1, null, null, null));
        Assert.Null(StrategicProductionController.GroundingFailure(goal, "observation", SiloCatalogs.Rocket(), automation: true));
        Assert.NotNull(StrategicProductionController.GroundingFailure(goal, "observation", SiloCatalogs.Rocket(siloEnabled: false), automation: true));
    }

    [Fact]
    public void TheSiloChestKeepsItsPlannedBufferAndNothingIsCollected()
    {
        var state = new FactoryState(1, "world", [], [Cell()]).WithTarget("rocket-part", 1);
        var shares = FactoryLogistics.CellShares(SiloCatalogs.Rocket(), state)!;
        Assert.Equal(1, shares["rocket-part"], 6);
        // Ten minutes of one part a minute, as planned assembler cells keep; fluid chain cells keep the caller's buffer.
        Assert.Equal(10, FactoryLogistics.CellBufferCrafts(Cell(), shares, 40));
        Assert.Equal(40, FactoryLogistics.CellBufferCrafts(Cell() with { Kind = FluidCellBuilder.MachineKind }, shares, 40));
        Assert.Empty(FactoryLogistics.OutputChests([Cell()]));
    }

    [Fact]
    public void ASiloCellTakesNoRecipeHasNoOutputSideAndOwnsAOneSlotBand()
    {
        // entities.lua: the silo's fixed_recipe is rocket-part; the engine needs no set_recipe, and parts never leave as items.
        Assert.False(FactoryCellBuilder.Configured(SiloCellPlanner.Kind, "rocket-part"));
        Assert.True(FactoryCellBuilder.Configured("assembler", "iron-gear-wheel"));
        Assert.Equal((true, false), FactoryCellBuilder.Sides(SiloCellPlanner.Kind));
        Assert.Equal((true, true), FactoryCellBuilder.Sides("assembler"));
        Assert.Equal((false, false), FactoryCellBuilder.Sides("lab"));
        Assert.Equal(SiloCellPlanner.MaximumCells, FactoryCellBuilder.Slots(SiloCellPlanner.Kind));
        Assert.Equal(FactoryCellBuilder.ZoneSlots, FactoryCellBuilder.Slots("assembler"));
    }

    [Fact]
    public void TheSiloIsFedFromOneChestThroughAnInserterItsPoleSupplies()
    {
        var grass = FactoryMaps.Grass(40);
        var map = grass with { Items = new Dictionary<string, PlaceableItem>(grass.Items) { ["rocket-silo"] = new("rocket-silo", 1) } };
        var silo = map.Prototypes["rocket-silo"];
        var origin = new MapPosition(-10, -20);
        var layout = new FactoryBandPlanner().Layout(map, new("rocket-silo", "inserter", "iron-chest", "small-electric-pole"), origin,
            new(0, 0, true), input: true, output: false);
        Assert.Equal(["input-chest", "input-inserter", "machine", "pole"], layout.Entities.Select(e => e.Role).Order());
        var body = silo.CollisionBox.Translate(layout.Machine.Position);
        var arm = map.Prototypes["inserter"];
        var input = layout.Role("input-inserter")!;
        Assert.True(body.Contains(At(input, arm.InserterDrop!)));
        Assert.Equal(Tile(layout.Role("input-chest")!.Position), Tile(At(input, arm.InserterPickup!)));
        var pole = map.Prototypes["small-electric-pole"];
        Assert.True(PowerGridPlanner.Supplies(layout.Role("pole")!.Position, pole, body));
        Assert.True(PowerGridPlanner.Supplies(layout.Role("pole")!.Position, pole, arm.CollisionBox.Translate(input.Position)));
        // A one-slot band of the silo's pitch holds the whole cell.
        Assert.True(new WorldBox(origin, new(origin.X + FactoryBandPlanner.Pitch(silo), origin.Y + FactoryBandPlanner.BandHeight(silo)))
            .Contains(layout.Footprint));
    }

    [Fact]
    public void TheActorOnlyBringsWhatTheCellLacksForTheNextFiveParts()
    {
        var recipe = SiloCatalogs.Rocket().Recipes.Single(r => r.Name == "rocket-part");
        var last = new RocketStep("supply", 1, new Dictionary<string, int> { ["processing-unit"] = 10, ["low-density-structure"] = 10, ["rocket-fuel"] = 10 });
        var stock = new Dictionary<string, long> { ["processing-unit"] = 4, ["low-density-structure"] = 12 };
        Assert.Equal(new Dictionary<string, int> { ["processing-unit"] = 6, ["low-density-structure"] = 0, ["rocket-fuel"] = 10 },
            SiloCellPlanner.Procurement(last, recipe, stock));
        // Ninety parts to go: one bounded batch of five parts, as hand supply delivers.
        var far = last with { RemainingCycles = 90, RequiredItems = last.RequiredItems.ToDictionary(p => p.Key, _ => 900) };
        Assert.All(SiloCellPlanner.Procurement(far, recipe, new Dictionary<string, long>()).Values, n => Assert.Equal(RocketPlanner.BatchCycles * 10, n));
        Assert.All(SiloCellPlanner.Procurement(last with { Kind = "wait" }, recipe, new Dictionary<string, long>()).Values, n => Assert.Equal(0, n));
    }

    [Fact]
    public void CellStockCountsTheChestAndTheInserterHandButNotTheSilo()
    {
        var snapshot = new FactorySnapshot("snapshot", Scope, 1, 2, Protocol.ToElement(new { }), [
            new("inventory:chest:1", "inventory", "chest", "chest", Protocol.ToElement(new { items = new Dictionary<string, long> { ["rocket-fuel"] = 7 } })),
            new("held:arm", "transit", "arm", "inserter-hand", Protocol.ToElement(new { items = new Dictionary<string, long> { ["rocket-fuel"] = 1 } })),
            new("inventory:silo:2", "inventory", "silo", "rocket_silo_input", Protocol.ToElement(new { items = new Dictionary<string, long> { ["rocket-fuel"] = 5 } }))]);
        Assert.Equal(8, SiloCellSupply.CellStock(snapshot, Cell())["rocket-fuel"]);
    }

    [Fact]
    public void LaunchPrefersTheReadySiloCellWhoseSiloIsObserved()
    {
        ObservedRocketSilo Observed(string id, int parts) => new(id, "rocket-silo", new(0, 0), "rocket-part", parts, "building_rocket",
            false, false, 1000, new Dictionary<string, long>(), new Dictionary<string, long>(), 1);
        var rockets = new RocketSnapshot(Scope, 1, 1, 0, true, true, new Dictionary<string, RocketSiloPrototype> { ["rocket-silo"] = SiloCatalogs.Silo },
            [Observed("loose", 90), Observed("silo", 10), Observed("unfinished", 0)]);
        var unfinished = Cell() with { Id = "cell-2", Status = "building", Entities = new Dictionary<string, string> { ["machine"] = "unfinished" } };
        var state = new FactoryState(1, "world", [], [unfinished, Cell()]);
        // The cell's inserter feeds its silo, even when a loose silo holds more parts.
        Assert.Equal("cell-1", SiloCellSupply.Registered(state, rockets, SiloCatalogs.Silo)?.Id);
        // A destroyed cell silo leaves the launch to other silos until maintenance rebuilds it.
        Assert.Null(SiloCellSupply.Registered(state, rockets with { Silos = [Observed("loose", 90)] }, SiloCatalogs.Silo));
        // No cell silo, finished or not, may be picked as a loose silo and fed by hand.
        Assert.Equal(new HashSet<string> { "unfinished", "silo" }, SiloCellSupply.Silos(state));
        Assert.Empty(SiloCellSupply.Silos(null));
    }

    [Fact]
    public async Task SiloCellFixtureCannotModifyANormalCampaign()
    {
        var session = new RuntimeSession("nonexistent-normal-campaign", "factorio.exe", "config.ini", "mods", "save.zip",
            0, 1, 2, "test-only", "session", "world", 1, false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new SiloCellQualification(session).RunAsync(CancellationToken.None));
        Assert.Contains("fixture", error.Message);
    }

    private static FactoryCell Cell() => new("cell-1", 2, new(0, 0, true), SiloCellPlanner.Kind, "rocket-silo", "rocket-part",
        new Dictionary<string, string> { ["machine"] = "silo", ["input-inserter"] = "arm", ["input-chest"] = "chest", ["pole"] = "pole" }, "ready", 1);

    private static GameResponse Response(ProductionCatalog catalog) => new(1, "request", true, catalog.CollectedTick, Protocol.ToElement(catalog));

    private static MapPosition At(PlannedEntity e, MapPosition offset)
    {
        var rotated = ExtractionPlanner.Rotate(offset, e.Direction);
        return new(e.Position.X + rotated.X, e.Position.Y + rotated.Y);
    }
    private static (double, double) Tile(MapPosition p) => (Math.Floor(p.X), Math.Floor(p.Y));
}

/// <summary>The advanced oil catalog plus recipe.lua rocket parts, silo, structures and rocket fuel, and the entities.lua silo.</summary>
internal static class SiloCatalogs
{
    // entities.lua: rocket_parts_required 100 and crafting_speed 1; 2.0.77 reports its 3,990 kW active usage as maximum energy per tick.
    public static readonly RocketSiloPrototype Silo = new("rocket-silo", "rocket-part", 100, 1, 66500);

    public static ProductionCatalog Rocket(bool siloEnabled = true)
    {
        var advanced = OilCatalogs.Advanced();
        static NativeMaterial Item(string name, double amount) => new(name, "item", amount);
        static NativeMaterial Fluid(string name, double amount) => new(name, "fluid", amount);
        return advanced with
        {
            // technology.lua: the rocket-silo technology unlocks both the silo and its part.
            Recipes = [.. advanced.Recipes,
                new("rocket-silo", siloEnabled, "crafting", 30, [Item("steel-plate", 1000), Item("processing-unit", 200)], [Item("rocket-silo", 1)], false),
                new("rocket-part", siloEnabled, "rocket-building", 3,
                    [Item("processing-unit", 10), Item("low-density-structure", 10), Item("rocket-fuel", 10)], [Item("rocket-part", 1)], true),
                new("low-density-structure", true, "crafting", 15, [Item("steel-plate", 2), Item("copper-plate", 20), Item("plastic-bar", 5)],
                    [Item("low-density-structure", 1)], false),
                new("rocket-fuel", true, "crafting-with-fluid", 15, [Item("solid-fuel", 10), Fluid("light-oil", 10)], [Item("rocket-fuel", 1)], false)],
            Items = new Dictionary<string, NativeItem>(advanced.Items)
            {
                ["rocket-silo"] = new(0, 1, PlaceEntity: "rocket-silo", PlaceEntityType: "rocket-silo"), ["rocket-part"] = new(0, 5),
                ["low-density-structure"] = new(0, 50), ["rocket-fuel"] = new(0, 20), ["solid-fuel"] = new(0, 50)
            },
            Silos = new Dictionary<string, RocketSiloPrototype> { ["rocket-silo"] = Silo }
        };
    }
}
