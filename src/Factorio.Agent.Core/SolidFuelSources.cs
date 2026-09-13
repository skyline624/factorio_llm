namespace Factorio.Agent.Core;

/// <summary>Classifies native solid extraction sources without treating trees as ordinary industrial fuel.</summary>
public static class SolidFuelSources
{
    public static bool CanExtract(ProductionCatalog catalog, string item, bool allowManualBootstrap = false) =>
        catalog.Mining.Any(source =>
            (allowManualBootstrap || catalog.MiningSourceTypes?.GetValueOrDefault(source.Key) == "resource")
            && source.Value.Any(product => product.Name == item && product.DeterministicItem));
}
