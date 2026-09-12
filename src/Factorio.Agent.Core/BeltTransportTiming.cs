namespace Factorio.Agent.Core;

public static class BeltTransportTiming
{
    public static long ObservationBudget(int quantity, int beltCount, double beltSpeed, double sourceSecondsPerItem, double targetSecondsPerItem)
    {
        if (quantity < 1 || beltCount < 1 || !double.IsFinite(beltSpeed) || beltSpeed <= 0
            || !double.IsFinite(sourceSecondsPerItem) || sourceSecondsPerItem < 0
            || !double.IsFinite(targetSecondsPerItem) || targetSecondsPerItem < 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Native transport rates must be finite and positive.");
        double processingTicks = quantity * 60d * Math.Max(2, sourceSecondsPerItem + targetSecondsPerItem);
        return (long)Math.Clamp(Math.Ceiling(1200 + beltCount / beltSpeed + processingTicks), 3600, 180000);
    }

    public static double SecondsPerItem(MaterialEndpoint endpoint, SpatialSnapshot map, ProductionCatalog catalog)
    {
        if (endpoint.Recipe is null) return 0;
        string name = map.Entities.Single(e => e.Id == endpoint.EntityId).Name;
        double speed = catalog.Assemblers?.Values.SingleOrDefault(m => m.EntityName == name)?.CraftingSpeed
            ?? catalog.Machines.Values.SingleOrDefault(m => m.EntityName == name)?.CraftingSpeed
            ?? throw new InvalidDataException("Missing native endpoint crafting speed.");
        if (!double.IsFinite(speed) || speed <= 0 || endpoint.UnitsPerCycle <= 0)
            throw new InvalidDataException("Invalid native endpoint material rate.");
        return catalog.Recipes.Single(r => r.Name == endpoint.Recipe).EnergySeconds / speed / endpoint.UnitsPerCycle;
    }
}
