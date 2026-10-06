using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SmeltingExpansionPlannerTests
{
    [Fact]
    public void APairDeliversTheSlowerOfNativeExtractionAndFurnaceProcessing()
    {
        var (map, catalog, plan) = SmeltingFuelPlannerTests.Setup();
        var furnace = catalog.Machines["furnace-item"];
        // A burner drill mines 0.25 ore/s; the furnace could take 1/3.2 = 0.3125.
        Assert.Equal(.25, SmeltingExpansionPlanner.PairRate(plan, map, catalog, furnace), 6);
        var faster = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["drill"] = map.Prototypes["drill"] with { MiningSpeed = .5 } } };
        Assert.Equal(1 / 3.2, SmeltingExpansionPlanner.PairRate(plan, faster, catalog, furnace), 6);
    }

    [Fact]
    public void OnlyAWaitLongerThanTheTargetIsWorthAnotherPair()
    {
        // The observed 236-plate wait: one burner pair needs almost 16 game minutes, four need under four.
        Assert.Equal(3, SmeltingExpansionPlanner.AdditionalPairs(236, .25, 1));
        Assert.Equal(2, SmeltingExpansionPlanner.AdditionalPairs(236, .25, 2));
        Assert.Equal(0, SmeltingExpansionPlanner.AdditionalPairs(60, .25, 1));
        Assert.Equal(1, SmeltingExpansionPlanner.AdditionalPairs(61, .25, 1));
        Assert.Equal(3, SmeltingExpansionPlanner.AdditionalPairs(1000, .25, 1));
        Assert.Equal(0, SmeltingExpansionPlanner.AdditionalPairs(1000, .25, SmeltingExpansionPlanner.MaximumPairs));
        Assert.Equal(0, SmeltingExpansionPlanner.AdditionalPairs(0, .25, 1));
    }
}
