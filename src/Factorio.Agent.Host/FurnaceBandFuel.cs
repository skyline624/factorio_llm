using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Fuel kept in the input chests of band furnaces. Their input inserter loads the fuel slot natively, so logistics
/// never fuels them by hand; each reserve covers the buffered crafts from native recipe work and furnace usage.
/// </summary>
public static class FurnaceBandFuel
{
    /// <summary>Distinct furnace items of ready band cells, whose native geometry sizes the reserves.</summary>
    public static IReadOnlyList<string> MachineItems(IEnumerable<FactoryCell> cells) => Served(cells)
        .Select(c => c.MachineItem).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Fuel units each ready band furnace's input chest keeps for the buffered crafts, by cell id.</summary>
    public static IReadOnlyDictionary<string, long> Reserves(ProductionCatalog catalog, SpatialSnapshot map, IEnumerable<FactoryCell> cells,
        int crafts, string fuel) => Served(cells).ToDictionary(c => c.Id, c => (long)FurnaceCellPlanner.FuelReserve(
            catalog.Recipes.Single(r => r.Name == c.Recipe), catalog.Machines[c.MachineItem],
            map.Prototypes[map.Items[c.MachineItem].EntityName], catalog.Items[fuel], crafts), StringComparer.Ordinal);

    /// <summary>Reads furnace geometry only when band furnaces exist, so factories without them make no extra request.</summary>
    public static async Task<IReadOnlyDictionary<string, long>> ReservesAsync(IGameClient game, ProductionCatalog catalog,
        IReadOnlyList<FactoryCell> cells, int crafts, string fuel, CancellationToken token)
    {
        var machines = MachineItems(cells);
        if (machines.Count == 0) return new Dictionary<string, long>();
        var map = await new SpatialClient(game).CaptureAsync(machines, 4, token);
        if (map.Scope != catalog.Scope) throw new InvalidDataException("Actor identity changed while sizing band furnace fuel.");
        return Reserves(catalog, map, cells, crafts, fuel);
    }

    private static IEnumerable<FactoryCell> Served(IEnumerable<FactoryCell> cells) => cells.Where(c => c.Kind == FurnaceCellPlanner.Kind
        && c.Status == "ready" && c.Recipe is not null && c.Entities.ContainsKey("input-chest"));
}
