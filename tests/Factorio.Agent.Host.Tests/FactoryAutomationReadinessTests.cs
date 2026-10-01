using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryAutomationReadinessTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingEquipmentUsesRecipeProductsAndNativeUnlocksRatherThanTechnologyNames(bool available)
    {
        var catalog = Catalog();
        var technology = Technology("manufacturing-variant", "assembler-recipe-alias") with { Available = available };
        var readiness = FactoryDirector.Readiness(catalog, Technologies(technology, Technology("weapons", "turret-recipe")));

        Assert.False(FactoryDirector.Available(catalog));
        Assert.Equal(["assembling-machine-1"], readiness.MissingEquipment);
        var unlock = Assert.Single(readiness.UnlockResearch);
        Assert.Equal("manufacturing-variant", unlock.Technology);
        Assert.Equal(available, unlock.Available);
        Assert.Equal(["assembling-machine-1"], unlock.Equipment);
    }

    [Fact]
    public void EnabledEquipmentDoesNotRequestResearchOrClaimInstalledProduction()
    {
        var catalog = Catalog(assemblerEnabled: true);
        var readiness = FactoryDirector.Readiness(catalog, Technologies(Technology("manufacturing-variant", "assembler-recipe-alias")));

        Assert.True(FactoryDirector.Available(catalog));
        Assert.Empty(readiness.MissingEquipment);
        Assert.Empty(readiness.UnlockResearch);
    }

    [Fact]
    public void ATechnologyNamedAutomationIsNotAssumedToUnlockTheMissingMachine()
    {
        var readiness = FactoryDirector.Readiness(Catalog(), Technologies(Technology("automation", "turret-recipe")));

        Assert.Equal(["assembling-machine-1"], readiness.MissingEquipment);
        Assert.Empty(readiness.UnlockResearch);
    }

    [Fact]
    public void EquipmentMatchingUsesAllNativeEffectsBeforeTheDisplayListIsBounded()
    {
        string[] recipes = Enumerable.Range(0, 12).Select(i => $"unrelated-{i}").Append("assembler-recipe-alias").ToArray();
        var technology = Technology("manufacturing-variant", recipes);
        var readiness = FactoryDirector.Readiness(Catalog(), Technologies(technology));

        Assert.Equal(12, StrategicProductionController.Unlocks(technology.Effects).Length);
        Assert.Equal("manufacturing-variant", Assert.Single(readiness.UnlockResearch).Technology);
    }

    [Fact]
    public void CompletedResearchIsNotSuggestedAgainWhenItsRecipeIsStillUnavailable()
    {
        var technology = Technology("manufacturing-variant", "assembler-recipe-alias") with { Researched = true };
        var readiness = FactoryDirector.Readiness(Catalog(), Technologies(technology));

        Assert.Equal(["assembling-machine-1"], readiness.MissingEquipment);
        Assert.Empty(readiness.UnlockResearch);
    }

    private static ProductionCatalog Catalog(bool assemblerEnabled = false) => new(new("world", "session", "actor", 1, 1), 30,
        [Recipe("assembler-recipe-alias", "assembling-machine-1", assemblerEnabled),
         Recipe("inserter", "inserter", true), Recipe("small-electric-pole", "small-electric-pole", true), Recipe("lab", "lab", true)],
        new Dictionary<string, NativeItem>(), new Dictionary<string, NativeMaterial[]>(),
        new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());

    private static NativeRecipe Recipe(string name, string item, bool enabled) =>
        new(name, enabled, "crafting", 0.5, [], [new(item, "item", 1)], false);

    private static NativeTechnology Technology(string name, params string[] recipes) => new(name, true, false, true, [],
        [new("red", 1)], 10, 600, Effects: Protocol.ToElement(recipes.Select(recipe => new { type = "unlock-recipe", recipe }).ToArray()));

    private static IReadOnlyDictionary<string, NativeTechnology> Technologies(params NativeTechnology[] technologies) =>
        technologies.ToDictionary(t => t.Name, StringComparer.Ordinal);
}
