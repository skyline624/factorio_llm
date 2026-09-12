using Factorio.Agent.Core;
using Factorio.Agent.Host;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FurnaceSelectionTests
{
    [Fact]
    public void LoadedOreRemainsBoundToItsObservedFurnaceDespiteACloserEmptyOne()
    {
        var (map, catalog) = SmeltingPlannerTests.Setup(installed: true);
        var state = new ProductionState(map.Scope, map.CollectedTick, "ai", new Dictionary<string, long>(),
        [new("near", "furnace", new(1, 0), null, Protocol.ToElement(new { })),
         new("loaded", "furnace", new(40, 0), catalog.Recipes[0].Name,
             Protocol.ToElement(new { input = new { items = new Dictionary<string, int> { [catalog.Recipes[0].Ingredients[0].Name] = 28 } } }))]);
        Assert.Equal("loaded", ProductionController.SelectFurnace(catalog.Recipes[0], catalog, state, new(0, 0), "loaded")?.Id);
        Assert.Equal("near", ProductionController.SelectFurnace(catalog.Recipes[0], catalog, state, new(0, 0), null)?.Id);
    }

    [Fact]
    public void MissingBoundFurnaceDoesNotSilentlyUseAnotherMachinesInventories()
    {
        var (map, catalog) = SmeltingPlannerTests.Setup(installed: true);
        var state = new ProductionState(map.Scope, map.CollectedTick, "ai", new Dictionary<string, long>(),
            [new("near", "furnace", new(1, 0), null, Protocol.ToElement(new { }))]);
        Assert.Null(ProductionController.SelectFurnace(catalog.Recipes[0], catalog, state, new(0, 0), "destroyed"));
    }
}
