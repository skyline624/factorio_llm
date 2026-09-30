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

    [Fact]
    public void ReadyCellsAndUnsuppliableItemsAreNotSeeded()
    {
        var row = new ResourceRow(1, "smelter", "iron-plate", "iron-ore", new("burner-mining-drill", "iron-chest", "stone-furnace", "inserter",
            "small-electric-pole"), new(0, 0), 0, 3, 2, 15);
        var cell = new FactoryCell("iron", 0, new(1, 0, true), "smelter", "burner-mining-drill", "iron-plate",
            new Dictionary<string, string>(), "ready", 1);
        var raw = new Dictionary<string, double>(ScienceRaw) { ["invented-fluid"] = 5 };
        var seeds = FactoryDirector.RawSeeds(Catalogs.Raw(), State([cell], [row]), raw, new Dictionary<string, long>());
        Assert.Equal(["coal", "copper-plate"], seeds.Select(s => s.Item).Order(StringComparer.Ordinal));
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

    private static FactoryState State(IReadOnlyList<FactoryCell>? cells = null, IReadOnlyList<ResourceRow>? rows = null) =>
        new(1, "world", [], cells ?? [], rows ?? []);
}
