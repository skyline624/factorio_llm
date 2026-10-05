using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class RawSeedTests
{
    private static readonly IReadOnlyDictionary<string, double> ScienceRaw = new Dictionary<string, double>
    {
        ["iron-plate"] = 24, ["copper-plate"] = 12
    };

    [Fact]
    public void AFactoryWithoutResourceCellsSeedsEachPlannedPlateAndItsFurnaceCoal()
    {
        // Campaign 2026-09-30 (seed 20261002): science assemblers started on the actor's single early drill and furnace.
        var seeds = FactoryDirector.RawSeeds(Catalogs.Raw(), State(), ScienceRaw, new Dictionary<string, long> { ["iron-plate"] = 9 });
        Assert.Equal(["coal", "copper-plate", "iron-plate"], seeds.Select(s => s.Item).Order(StringComparer.Ordinal));
        Assert.Equal(24, seeds.Single(s => s.Item == "iron-plate").PerMinute);
        Assert.Equal(RawCapacityGrowth.DefaultPerMinute, seeds.Single(s => s.Item == "coal").PerMinute);
    }

    [Theory]
    [InlineData(30, false)]
    [InlineData(15, true)]
    public void CellsCoveringThePlannedRateAndUnsuppliableItemsAreNotSeeded(double cellPerMinute, bool seeded)
    {
        // One 15-plate cell under a 24-plate plan still leaves the assemblers starved, so the rate decides, not a cell count.
        var row = new ResourceRow(1, "smelter", "iron-plate", "iron-ore", new("burner-mining-drill", "iron-chest", "stone-furnace", "inserter",
            "small-electric-pole"), new(0, 0), 0, 3, 2, cellPerMinute);
        var cell = new FactoryCell("iron", 0, new(1, 0, true), "smelter", "burner-mining-drill", "iron-plate",
            new Dictionary<string, string>(), "ready", 1);
        var raw = new Dictionary<string, double>(ScienceRaw) { ["invented-fluid"] = 5 };
        var seeds = FactoryDirector.RawSeeds(Catalogs.Raw(), State([cell], [row]), raw, new Dictionary<string, long>());
        Assert.Equal(seeded ? ["coal", "copper-plate", "iron-plate"] : ["coal", "copper-plate"], seeds.Select(s => s.Item).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CarriedStockCoveringTheHorizonLetsAssemblersStartAtOnce()
    {
        // The prepared factory research carries 400 iron and 100 copper plates and must not hand-craft any drill.
        var carried = new Dictionary<string, long> { ["iron-plate"] = 400, ["copper-plate"] = 100 };
        Assert.Empty(FactoryDirector.RawSeeds(Catalogs.Raw(), State(), new Dictionary<string, double> { ["iron-plate"] = 12, ["copper-plate"] = 6 }, carried));
        Assert.Equal(["coal", "copper-plate"], FactoryDirector.RawSeeds(Catalogs.Raw(), State(), ScienceRaw, carried)
            .Select(s => s.Item).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DepletedCoalIsReplacedEvenWhenExistingSmeltersCoverEveryPlannedPlate()
    {
        // Normal seed20261072: two electric iron cells and the copper cell covered red science,
        // but depleted coal was repeatedly procured in temporary21-item lots before green preparation.
        var seeds = FactoryDirector.RawSeeds(Catalogs.Raw(), CoveredPlates(), ScienceRaw, new Dictionary<string, long>());
        var coal = Assert.Single(seeds);
        Assert.Equal("coal", coal.Item);
        Assert.Equal(RawCapacityGrowth.DefaultPerMinute, coal.PerMinute);
    }

    [Fact]
    public void CarriedCoalForTheHorizonDoesNotReplaceItsDepletedProducerDuringPreparation()
    {
        var carried = new Dictionary<string, long> { ["coal"] = (long)(RawCapacityGrowth.DefaultPerMinute * FactoryDirector.SeedHorizonMinutes) };
        Assert.Empty(FactoryDirector.RawSeeds(Catalogs.Raw(), CoveredPlates(), ScienceRaw, carried));
    }

    [Fact]
    public void StockOnlyPreparationDoesNotSeedCoalForInactiveSmelters()
    {
        var state = CoveredPlates();
        state = state with { Cells = state.Cells.Select(c => c with { Status = "depleted" }).ToArray() };
        var carried = ScienceRaw.ToDictionary(p => p.Key, p => (long)(p.Value * FactoryDirector.SeedHorizonMinutes));
        Assert.Empty(FactoryDirector.RawSeeds(Catalogs.Raw(), state, ScienceRaw, carried));
    }

    [Fact]
    public void ExplicitRawRateBuildsCapacityEvenWhenCarriedStockCoversItsHorizon()
    {
        var raw = new Dictionary<string, double> { ["coal"] = 30 };
        var state = State() with { Targets = raw };
        var seeds = FactoryDirector.RawSeeds(Catalogs.Raw(), state, raw, new Dictionary<string, long> { ["coal"] = 500 });
        var coal = Assert.Single(seeds);
        Assert.Equal("coal", coal.Item);
        Assert.Equal(30, coal.PerMinute);
    }

    [Fact]
    public void ExplicitRawRateKeepsCapacityThatAlreadyCoversTheDemand()
    {
        var raw = new Dictionary<string, double> { ["coal"] = 15 };
        var state = CoveredPlates() with { Targets = raw };
        state = state with { Cells = state.Cells.Select(c => c.Recipe == "coal" ? c with { Status = "ready" } : c).ToArray() };
        Assert.Empty(FactoryDirector.RawSeeds(Catalogs.Raw(), state, raw, new Dictionary<string, long>()));
    }

    [Fact]
    public void NativeChestAndFinishedOutputsCoverPreparationWithoutCountingCommittedInventories()
    {
        FactoryRecord Stock(string id, string name, object items) => new(id, "inventory", id, name, Protocol.ToElement(new { items }));
        var accessible = new[] { Stock("bag", "character_main", new Dictionary<string, long> { ["iron-plate"] = 4 }),
            Stock("chest", "chest", new Dictionary<string, long> { ["iron-plate"] = 120, ["copper-plate"] = 120 }),
            Stock("output", "crafter_output", new Dictionary<string, long> { ["iron-plate"] = 116 }) };
        var committed = new[] { "character_corpse", "crafter_input", "fuel", "turret_ammo", "lab_input", "transit" }
            .Select(name => Stock(name, name, new Dictionary<string, long> { ["iron-plate"] = 10000, ["copper-plate"] = 10000, ["coal"] = 10000 })).ToArray();
        var snapshot = new FactorySnapshot("stock", new("world", "session", "actor", 1, 1), 100, 200,
            Protocol.ToElement(new { }), [.. accessible, .. committed]);
        var available = FactoryLogistics.AvailableStock(snapshot);
        Assert.Equal(240, available["iron-plate"]);
        Assert.Equal(120, available["copper-plate"]);
        Assert.False(available.ContainsKey("coal"));
        Assert.Empty(FactoryDirector.RawSeeds(Catalogs.Raw(), State(), ScienceRaw, available));
        Assert.Equal(["coal", "copper-plate", "iron-plate"], FactoryDirector.RawSeeds(Catalogs.Raw(), State(), ScienceRaw,
            FactoryLogistics.AvailableStock(snapshot with { Records = committed })).Select(s => s.Item));
    }

    [Fact]
    public void AnIndependentRawTargetWithNativeBufferGrowsAfterTheRequestedChain()
    {
        var targets = new Dictionary<string, double> { ["automation-science-pack"] = 6, ["copper-plate"] = 12 };
        var state = State() with { Targets = targets };
        var available = new Dictionary<string, long> { ["iron-plate"] = 240, ["copper-plate"] = 120, ["coal"] = 500 };
        Assert.Empty(FactoryDirector.RawSeeds(Catalogs.Raw(), state, ScienceRaw, available, priorityItem: "automation-science-pack"));
        // The later pass still requests the full explicit rate; stock is never certified as sustainable capacity.
        Assert.Equal(("copper-plate", 12d), Assert.Single(FactoryDirector.RawSeeds(Catalogs.Raw(), state, ScienceRaw, available)));
        Assert.Equal(targets, state.Targets);
    }

    [Theory]
    [InlineData("copper-plate")]
    [InlineData("coal")]
    public void TheRequestedRawTargetAlwaysRequiresCapacityDespiteALargeBuffer(string item)
    {
        var raw = new Dictionary<string, double> { [item] = 30 };
        var state = State() with { Targets = raw };
        var available = new Dictionary<string, long> { [item] = 10000, ["coal"] = 10000 };
        Assert.Equal((item, 30d), Assert.Single(FactoryDirector.RawSeeds(Catalogs.Raw(), state, raw, available, priorityItem: item)));
    }

    [Fact]
    public void AnIndependentRawTargetWithoutItsHorizonBufferStillGrowsBeforeTheRequestedChain()
    {
        var state = State() with { Targets = new Dictionary<string, double> { ["automation-science-pack"] = 6, ["copper-plate"] = 12 } };
        var available = new Dictionary<string, long> { ["iron-plate"] = 240, ["copper-plate"] = 119, ["coal"] = 500 };
        Assert.Equal(("copper-plate", 12d), Assert.Single(FactoryDirector.RawSeeds(Catalogs.Raw(), state, ScienceRaw, available,
            priorityItem: "automation-science-pack")));
    }

    [Fact]
    public void BoilerFuelCapacityRemainsMandatoryDuringBufferedPriorityPreparation()
    {
        var state = State() with { Targets = new Dictionary<string, double> { ["automation-science-pack"] = 6 } };
        var available = new Dictionary<string, long> { ["iron-plate"] = 240, ["copper-plate"] = 120, ["coal"] = 10000 };
        Assert.Equal(("coal", 45d), Assert.Single(FactoryDirector.RawSeeds(Catalogs.Raw(), state, ScienceRaw, available,
            powerFuelPerMinute: 45, priorityItem: "automation-science-pack")));
    }

    [Fact]
    public void PreparationCannotPrioritizeAnUnregisteredObjective() =>
        Assert.Throws<ArgumentException>(() => FactoryDirector.RawSeeds(Catalogs.Raw(), State(), ScienceRaw,
            new Dictionary<string, long>(), priorityItem: "automation-science-pack"));

    private static FactoryState CoveredPlates()
    {
        var smelter = new ResourceCellEquipment("electric-mining-drill", "iron-chest", "stone-furnace", "inserter", "small-electric-pole");
        var miner = new ResourceCellEquipment("burner-mining-drill", "iron-chest");
        var rows = new ResourceRow[]
        {
            new(1, "smelter", "iron-plate", "iron-ore", smelter, new(0, 0), 0, 3, 2, 18.75),
            new(2, "smelter", "copper-plate", "copper-ore", smelter, new(20, 0), 0, 3, 1, 15),
            new(3, "miner", "coal", "coal", miner, new(40, 0), 0, 3, 1, 15)
        };
        FactoryCell Cell(string id, int row, int index, string kind, string product, string status) =>
            new(id, 0, new(row, index, true), kind, kind == "smelter" ? "electric-mining-drill" : "burner-mining-drill", product,
                new Dictionary<string, string>(), status, 1);
        return State([Cell("iron-a", 1, 0, "smelter", "iron-plate", "ready"), Cell("iron-b", 1, 1, "smelter", "iron-plate", "ready"),
            Cell("copper", 2, 0, "smelter", "copper-plate", "ready"), Cell("coal", 3, 0, "miner", "coal", "depleted")], rows);
    }

    private static FactoryState State(IReadOnlyList<FactoryCell>? cells = null, IReadOnlyList<ResourceRow>? rows = null) =>
        new(1, "world", [], cells ?? [], rows ?? []);
}
