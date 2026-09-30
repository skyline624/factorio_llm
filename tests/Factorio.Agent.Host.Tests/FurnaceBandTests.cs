using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FurnaceBandTests
{
    private static readonly ActorScope Scope = new("world", "session", "actor", 1, 1);
    private static readonly HashSet<string> Machines = new(StringComparer.Ordinal) { "assembling-machine-1", "stone-furnace" };

    [Fact]
    public void SteelBecomesAFurnaceStageFedWithPlates()
    {
        var plan = AutomationPlanner.Plan(Catalogs.Raw(), "steel-plate", 3, Machines);
        var steel = Assert.Single(plan.Stages);
        Assert.Equal(("steel-plate", "steel-plate", FurnaceCellPlanner.Kind, "stone-furnace", 1),
            (steel.Recipe, steel.Item, steel.Kind, steel.MachineItem, steel.Machines));
        Assert.Equal(3, steel.CraftsPerMinute, 6);
        Assert.Equal(15, plan.RawPerMinute["iron-plate"], 6);
    }

    [Fact]
    public void FurnaceCountFollowsNativeCraftingSpeed()
    {
        // 16 s per steel at crafting speed 1 is 3.75 crafts per minute; one arm carries 9.6 crafts of five plates.
        Assert.Equal(3, AutomationPlanner.Plan(Catalogs.Raw(), "steel-plate", 10, Machines).Stages.Single().Machines);
        var fast = WithSteelFurnace(Catalogs.Raw(), enabled: true);
        var stage = AutomationPlanner.Plan(fast, "steel-plate", 10, new HashSet<string> { "steel-furnace" }).Stages.Single();
        Assert.Equal(("steel-furnace", 2), (stage.MachineItem, stage.Machines));
    }

    [Theory]
    [InlineData("iron-plate")]
    [InlineData("stone-brick")]
    public void OreSmeltingStaysWithResourceCellsOnThePatch(string item)
    {
        var plan = AutomationPlanner.Plan(Catalogs.Raw(), item, 30, Machines);
        Assert.Empty(plan.Stages);
        Assert.Equal(30, plan.RawPerMinute[item], 6);
    }

    [Fact]
    public void IntermediatesChainSteelCellsInsteadOfTreatingSteelAsRaw()
    {
        var plan = AutomationPlanner.Plan(Engines(), "engine-unit", 6, Machines);
        Assert.Equal(["engine-unit", "iron-gear-wheel", "pipe", "steel-plate"], plan.Stages.Select(s => s.Recipe));
        Assert.All(plan.Stages.Where(s => s.Recipe != "steel-plate"), s => Assert.Equal("assembler", s.Kind));
        var steel = plan.Stages.Single(s => s.Recipe == "steel-plate");
        Assert.Equal((FurnaceCellPlanner.Kind, 2), (steel.Kind, steel.Machines));
        Assert.Equal(["iron-plate"], plan.RawPerMinute.Keys);
        Assert.Equal(6 * 5 + 6 * 2 + 12, plan.RawPerMinute["iron-plate"], 6);
    }

    [Fact]
    public void WithoutAFurnaceMachineSteelStaysRaw()
    {
        var plan = AutomationPlanner.Plan(Catalogs.Raw(), "steel-plate", 3, new HashSet<string> { "assembling-machine-1" });
        Assert.Empty(plan.Stages);
        Assert.Equal(3, plan.RawPerMinute["steel-plate"], 6);
    }

    [Fact]
    public void TheFastestEnabledFurnaceThatBurnsTheFuelIsChosen()
    {
        var catalog = Catalogs.Raw();
        Assert.Equal("stone-furnace", FurnaceCellPlanner.Machine(catalog, "coal"));
        Assert.Equal("steel-furnace", FurnaceCellPlanner.Machine(WithSteelFurnace(catalog, enabled: true), "coal"));
        Assert.Equal("stone-furnace", FurnaceCellPlanner.Machine(WithSteelFurnace(catalog, enabled: false), "coal"));
        var nuclear = catalog with
        {
            Machines = new Dictionary<string, NativeFurnace>
            {
                ["stone-furnace"] = catalog.Machines["stone-furnace"] with { FuelCategories = new Dictionary<string, bool> { ["nuclear"] = true } }
            }
        };
        Assert.Null(FurnaceCellPlanner.Machine(nuclear, "coal"));
        Assert.Equal(new HashSet<string> { "assembling-machine-1", "stone-furnace" }, FactoryDirector.MachineItems(catalog));
    }

    [Fact]
    public void BandCellsOnlyTakeEnabledSolidSmeltingThatTheirFurnaceBurnsCoalFor()
    {
        var catalog = Catalogs.Raw();
        Assert.Null(FurnaceCellPlanner.Failure(catalog, "stone-furnace", "steel-plate", "coal"));
        // Planning leaves bricks to stone patches, but an operator may still build a brick band cell.
        Assert.Null(FurnaceCellPlanner.Failure(catalog, "stone-furnace", "stone-brick", "coal"));
        Assert.NotNull(FurnaceCellPlanner.Failure(catalog, "assembling-machine-1", "steel-plate", "coal"));
        Assert.NotNull(FurnaceCellPlanner.Failure(catalog, "stone-furnace", "iron-gear-wheel", "coal"));
        Assert.NotNull(FurnaceCellPlanner.Failure(catalog, "stone-furnace", null, "coal"));
        Assert.NotNull(FurnaceCellPlanner.Failure(catalog, "stone-furnace", "steel-plate", "wood"));
        var locked = catalog with { Recipes = catalog.Recipes.Select(r => r.Name == "steel-plate" ? r with { Enabled = false } : r).ToArray() };
        Assert.NotNull(FurnaceCellPlanner.Failure(locked, "stone-furnace", "steel-plate", "coal"));
        // The furnace itself must be craftable: a machine the force cannot make is never chosen for new cells.
        var uncraftable = catalog with { Recipes = catalog.Recipes.Select(r => r.Name == "stone-furnace" ? r with { Enabled = false } : r).ToArray() };
        Assert.NotNull(FurnaceCellPlanner.Failure(uncraftable, "stone-furnace", "steel-plate", "coal"));
    }

    [Fact]
    public void FurnacesPickTheirRecipeFromInputSoOnlyOtherCellsAreConfigured()
    {
        Assert.True(FactoryCellBuilder.Configured("assembler", "iron-gear-wheel"));
        Assert.False(FactoryCellBuilder.Configured(FurnaceCellPlanner.Kind, "steel-plate"));
        Assert.False(FactoryCellBuilder.Configured("lab", null));
    }

    [Fact]
    public void ABurnerFurnaceCellProvesItsPowerThroughItsInputInserter()
    {
        var map = FactoryMaps.Grass(4);
        Assert.Equal("machine", FactoryCellBuilder.PowerProbe(map.Prototypes["assembling-machine-1"]));
        Assert.Equal("input-inserter", FactoryCellBuilder.PowerProbe(StoneFurnace()));
    }

    [Fact]
    public void OnlyReadyBandFurnacesWithAnInputChestKeepAFuelReserve()
    {
        var catalog = Catalogs.Raw();
        var map = FactoryMaps.Grass(4);
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { ["stone-furnace"] = StoneFurnace() } };
        var io = new Dictionary<string, string> { ["machine"] = "m", ["input-chest"] = "in", ["output-chest"] = "out" };
        FactoryCell[] cells =
        [
            new("steel", 1, new(0, 0, true), FurnaceCellPlanner.Kind, "stone-furnace", "steel-plate", io, "ready", 1),
            new("bricks", 1, new(0, 1, true), FurnaceCellPlanner.Kind, "stone-furnace", "stone-brick", io, "ready", 1),
            new("unfinished", 1, new(0, 2, true), FurnaceCellPlanner.Kind, "stone-furnace", "steel-plate", io, "building", 1),
            new("gears", 1, new(0, 3, true), "assembler", "assembling-machine-1", "iron-gear-wheel", io, "ready", 1)
        ];
        var reserves = FurnaceBandFuel.Reserves(catalog, map, cells, 40, "coal");
        Assert.Equal(new Dictionary<string, long> { ["steel"] = 15, ["bricks"] = 12 }, reserves);
        Assert.Equal(["stone-furnace"], FurnaceBandFuel.MachineItems(cells));
    }

    [Fact]
    public void ChestFuelCoversTheBufferedCraftsFromNativeFurnaceWork()
    {
        var catalog = Catalogs.Raw();
        var furnace = catalog.Machines["stone-furnace"];
        var coal = catalog.Items["coal"];
        // 40 steel crafts of 16 s at 90 kW: 57.6 MJ over 4 MJ coal is 14.4, rounded up.
        Assert.Equal(15, FurnaceCellPlanner.FuelReserve(Recipe(catalog, "steel-plate"), furnace, StoneFurnace(), coal, 40));
        // Half the burner effectivity doubles the fuel.
        Assert.Equal(29, FurnaceCellPlanner.FuelReserve(Recipe(catalog, "steel-plate"), furnace, StoneFurnace() with { BurnerEffectivity = .5 }, coal, 40));
        // 40 brick crafts need under three coal; the chest still keeps a quarter stack between visits.
        Assert.Equal(12, FurnaceCellPlanner.FuelReserve(Recipe(catalog, "stone-brick"), furnace, StoneFurnace(), coal, 40));
    }

    [Fact]
    public void MissingNativeFurnaceEnergyIsRefused()
    {
        var catalog = Catalogs.Raw();
        Assert.Throws<InvalidDataException>(() => FurnaceCellPlanner.FuelReserve(Recipe(catalog, "steel-plate"), catalog.Machines["stone-furnace"],
            StoneFurnace() with { EnergyPerTick = null }, catalog.Items["coal"], 40));
        Assert.Throws<InvalidDataException>(() => FurnaceCellPlanner.FuelReserve(Recipe(catalog, "steel-plate"), catalog.Machines["stone-furnace"],
            StoneFurnace(), catalog.Items["iron-plate"], 40));
    }

    [Fact]
    public void BandFurnacesAreFuelledThroughTheirInputChestNeverByHand()
    {
        var snapshot = new FactorySnapshot("snapshot", Scope, 100, 200, Protocol.ToElement(new { }), [
            Entity("band-furnace", "furnace", "inventory:band-furnace:1"), Inventory("inventory:band-furnace:1", "band-furnace"),
            Entity("patch-furnace", "furnace", "inventory:patch-furnace:1"), Inventory("inventory:patch-furnace:1", "patch-furnace")]);
        FactoryCell[] cells =
        [
            new("band", 1, new(0, 0, true), FurnaceCellPlanner.Kind, "stone-furnace", "steel-plate",
                new Dictionary<string, string> { ["machine"] = "band-furnace", ["input-chest"] = "in", ["output-chest"] = "out" }, "ready", 1),
            new("patch", 0, new(1, 0, true), "smelter", "stone-furnace", "iron-plate",
                new Dictionary<string, string> { ["furnace"] = "patch-furnace", ["output-chest"] = "plates" }, "ready", 1)
        ];
        Assert.Equal(["patch-furnace"], FactoryLogistics.Burners(snapshot, cells).Select(b => b.EntityId));
    }

    [Fact]
    public void TheFirstFurnaceCellPoleIsLinkedFromOutsideItsReservedBand()
    {
        // Live 2.0.77 fixture: the band west of the injected source, too far for the cell pole to wire itself.
        var map = FactoryMaps.Grass(40);
        var zone = new FactoryZone(1, new(-31, 2), 8, 3, FactoryBandPlanner.BandHeight(map.Prototypes["stone-furnace"]));
        var layout = new FactoryBandPlanner().Layout(map, new("stone-furnace", "inserter", "iron-chest", "small-electric-pole"), zone.Origin, new(0, 0, true));
        var pole = map.Prototypes["small-electric-pole"];
        foreach (var arm in new[] { layout.Role("input-inserter")!, layout.Role("output-inserter")! })
            Assert.True(PowerGridPlanner.Supplies(layout.Role("pole")!.Position, pole, map.Prototypes["inserter"].CollisionBox.Translate(arm.Position)));
        var at = layout.Role("pole")!.Position;
        var source = new SpatialEntity("src", "small-electric-pole", new(-18.5, 0.5), pole.CollisionBox.Translate(new(-18.5, 0.5)), 0, "agent", Power: new(1, 1));
        var cellPole = new SpatialEntity("cell", "small-electric-pole", at, pole.CollisionBox.Translate(at), 0, "agent", Power: new(0, 2));
        var reserved = FactoryCellBuilder.ReserveZone(FactoryMaps.Grass(40, [source, cellPole]), zone, "small-electric-pole");
        var link = new PowerGridPlanner().Next(reserved, "small-electric-pole", cellPole.Bounds, new HashSet<string> { "src" });
        Assert.Equal(PowerGridSearchStatus.Extension, link.Status);
        Assert.False(zone.Box.Contains(link.Pole!.Position));
    }

    [Fact]
    public void BurnerMachinesAddNoElectricDemandToANewCell()
    {
        var map = FactoryMaps.Grass(4);
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { ["stone-furnace"] = StoneFurnace() } };
        var equipment = new CellEquipment("stone-furnace", "inserter", "iron-chest", "small-electric-pole");
        Assert.Equal(2 * 245, PowerExpansionController.CellDemand(map, equipment, io: true), 6);
    }

    // entities.lua: stone furnace, 90 kW burner at effectivity 1, 2x2 tiles.
    internal static EntityGeometry StoneFurnace() => new("stone-furnace", "furnace", new(new(-0.7, -0.7), new(0.7, 0.7)),
        new(["item", "object", "player", "water_tile"], false, false, false), 2, 2,
        FuelCategories: new Dictionary<string, bool> { ["chemical"] = true }, EnergyPerTick: 1500, BurnerEffectivity: 1);

    private static NativeRecipe Recipe(ProductionCatalog catalog, string name) => catalog.Recipes.Single(r => r.Name == name);

    private static ProductionCatalog WithSteelFurnace(ProductionCatalog catalog, bool enabled) => catalog with
    {
        Recipes = [.. catalog.Recipes, new("steel-furnace", enabled, "crafting", 3,
            [new("steel-plate", "item", 6), new("stone-brick", "item", 10)], [new("steel-furnace", "item", 1)], false)],
        Items = new Dictionary<string, NativeItem>(catalog.Items)
        {
            ["steel-furnace"] = new(0, 50, PlaceEntity: "steel-furnace", PlaceEntityType: "furnace")
        },
        Machines = new Dictionary<string, NativeFurnace>(catalog.Machines)
        {
            ["steel-furnace"] = new("steel-furnace", new Dictionary<string, bool> { ["smelting"] = true },
                new Dictionary<string, bool> { ["chemical"] = true }, 2)
        }
    };

    /// <summary>Raw catalog plus pipes and engine units, whose advanced crafting the assembler accepts.</summary>
    private static ProductionCatalog Engines()
    {
        var raw = Catalogs.Raw();
        var assembler = raw.Assemblers!["assembling-machine-1"];
        return raw with
        {
            Recipes = [.. raw.Recipes,
                new("pipe", true, "crafting", 0.5, [new("iron-plate", "item", 1)], [new("pipe", "item", 1)], false),
                new("engine-unit", true, "advanced-crafting", 10,
                    [new("steel-plate", "item", 1), new("iron-gear-wheel", "item", 1), new("pipe", "item", 2)], [new("engine-unit", "item", 1)], false)],
            Assemblers = new Dictionary<string, NativeAssembler>
            {
                ["assembling-machine-1"] = assembler with { Categories = new Dictionary<string, bool>(assembler.Categories) { ["advanced-crafting"] = true } }
            }
        };
    }

    private static FactoryRecord Entity(string id, string type, string fuel) => new(id, "entity", id, type,
        Protocol.ToElement(new { role = "factory", type, fuelInventoryId = fuel }));

    private static FactoryRecord Inventory(string id, string owner) =>
        new(id, "inventory", owner, "fuel", Protocol.ToElement(new { role = "factory", items = new { } }));
}
