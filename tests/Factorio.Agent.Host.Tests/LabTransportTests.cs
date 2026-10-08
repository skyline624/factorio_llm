using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class LabTransportTests
{
    [Fact]
    public void LaboratoriesTakeEverySciencePackARegisteredCellMakesFromBuses()
    {
        // Until 2026-10-08 laboratories had no input side and were filled by the actor's tours.
        var catalog = Catalogs.Early();
        var state = State(Cell("red", "assembler", "automation-science-pack", ["output-chest"]), Cell("gears", "assembler", "iron-gear-wheel", ["output-chest"]),
            Cell("lab", "lab", null, ["machine", "input-chest"]));
        var recipe = FactoryTransportBuilder.InputRecipe(catalog, state, state.Cells.Single(c => c.Id == "lab"))!;
        Assert.Equal(["automation-science-pack"], recipe.Ingredients.Select(i => i.Name));
        Assert.All(recipe.Ingredients, i => Assert.True(i.DeterministicItem));
        Assert.Equal(FactoryTransportBuilder.LaboratoryPacksPerMinute,
            FactoryTransportBuilder.InputVolume(catalog, state.Cells.Single(c => c.Id == "lab"), recipe.Ingredients[0]));
        // Other cells keep their native recipe.
        Assert.Equal("automation-science-pack", FactoryTransportBuilder.InputRecipe(catalog, state, state.Cells.Single(c => c.Id == "red"))!.Name);
    }

    [Fact]
    public void NewLaboratoriesHaveAnInputSideAndOldOnesAreFitted()
    {
        Assert.Equal((true, false), FactoryCellBuilder.Sides("lab"));
        var old = Cell("old", "lab", null, ["machine"]);
        var fitted = Cell("fitted", "lab", null, ["machine", "input-chest"]);
        Assert.Equal(["old"], LabInputRetrofit.Pending(State(old, fitted)).Select(c => c.Id));
    }

    private static FactoryCell Cell(string id, string kind, string? recipe, string[] roles) =>
        new(id, 1, new(0, 0, true), kind, kind == "lab" ? "lab" : "assembling-machine-1", recipe,
            roles.ToDictionary(r => r, r => $"{id}:{r}"), "ready", 1,
            Plan: new Dictionary<string, PlannedEntity> { ["machine"] = new("machine", kind == "lab" ? "lab" : "assembling-machine-1", new(0.5, 0.5), 0) });

    private static FactoryState State(params FactoryCell[] cells) => new(1, "w", [new(1, new(0, 0), 4, 6, 6)], cells);
}
