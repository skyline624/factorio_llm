using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Why a row gets supply-line work next: "resume" an interrupted line, "extend" a ready one to new cells, or build a "new" one.</summary>
public sealed record SupplyLineNeed(int Row, string Product, string Reason, double Distance, int ReadyCells, string? Line);

/// <summary>
/// Registry view of supply lines. A line is a factory cell of kind <see cref="SupplyLinePlanner.Kind"/> whose slot band is its row id:
/// collector and trunk belts in flow order, one feeder per served row cell, the depot chest and its inserter, and link poles.
/// </summary>
internal static class SupplyLines
{
    private const string Collector = "collector-", Trunk = "trunk-", Feeder = "feeder-";

    /// <summary>The line registered for a row, whatever its status.</summary>
    public static FactoryCell? Line(FactoryState state, int row) =>
        state.Cells.FirstOrDefault(c => c.Kind == SupplyLinePlanner.Kind && c.Slot.Band == row);

    /// <summary>
    /// Row cells, as (row id, slot index), whose output a built, powered feeder of a ready line carries to its powered depot inserter.
    /// Only the given cells count, so a line whose parts are missing this round serves nothing and its row is collected directly, as
    /// is a cell whose feeder, or the whole row when the depot inserter, sits on a network without a generator.
    /// </summary>
    public static IReadOnlySet<(int Row, int Cell)> Served(IEnumerable<FactoryCell> cells, IReadOnlySet<string> unpowered) => cells
        .Where(c => c.Kind == SupplyLinePlanner.Kind && c.Status == "ready"
            && c.Entities.TryGetValue(SupplyLinePlanner.DepotInserterRole, out var depot) && !unpowered.Contains(depot))
        .SelectMany(c => FeederCells(c.Entities.Keys).Where(i => !unpowered.Contains(c.Entities[SupplyLinePlanner.FeederRole(i)]))
            .Select(index => (c.Slot.Band, index))).ToHashSet();

    /// <summary>Slot indexes of the row cells that have a feeder role among these roles.</summary>
    public static IReadOnlyList<int> FeederCells(IEnumerable<string> roles) => roles
        .Where(r => r.StartsWith(Feeder, StringComparison.Ordinal) && int.TryParse(r.AsSpan(Feeder.Length), out _))
        .Select(r => int.Parse(r.AsSpan(Feeder.Length))).Order().ToArray();

    /// <summary>Belt roles in flow order: the collector from the far end of the row, then the trunk toward the depot.</summary>
    public static IReadOnlyList<string> FlowRoles(IEnumerable<string> roles)
    {
        var all = roles.ToArray();
        return all.Where(r => r.StartsWith(Collector, StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .Concat(all.Where(r => r.StartsWith(Trunk, StringComparison.Ordinal)).Order(StringComparer.Ordinal)).ToArray();
    }

    public static bool IsTrunk(string role) => role.StartsWith(Trunk, StringComparison.Ordinal);

    /// <summary>Products that ready band cells take as recipe ingredients: what a depot beside a band is for.</summary>
    public static IReadOnlySet<string> Consumed(ProductionCatalog catalog, FactoryState state) => state.Cells
        .Where(c => c.Zone > 0 && c.Status == "ready" && c.Recipe is not null)
        .SelectMany(c => catalog.Recipes.FirstOrDefault(r => r.Name == c.Recipe)?.Ingredients.Select(i => i.Name) ?? [])
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The row that needs line work next, or null. Only smelter rows whose plates the bands consume, with at least one ready cell
    /// whose chest lies farther than <see cref="SupplyLinePlanner.MinimumDistance"/> from every band walkway, are considered. An
    /// interrupted line resumes first, then a ready line gains feeders for newly ready cells, then the row whose ready cells times
    /// distance saves the most walking gets a new line. Abandoned lines are left to direct collection.
    /// </summary>
    /// <param name="chests">Output chest positions of each row's planned cells by slot index, from the row's native template.</param>
    public static SupplyLineNeed? Next(FactoryState state, IReadOnlySet<string> consumed, IReadOnlyDictionary<int, IReadOnlyList<MapPosition>> chests)
    {
        if (state.Zones.Count == 0) return null;
        var walkways = state.Zones.Select(z => FactoryBandPlanner.Walkway(z.Origin, z.Slots, z.Pitch, z.BandHeight, z.TransportAccess)).ToArray();
        var needs = new List<SupplyLineNeed>();
        foreach (var row in (state.Rows ?? []).Where(r => r.Kind == "smelter" && consumed.Contains(r.Product) && chests.ContainsKey(r.Id)))
        {
            var ready = state.Cells.Where(c => c.IsResource && c.Slot.Band == row.Id && c.Status == "ready" && c.Slot.Index < chests[row.Id].Count)
                .Select(c => c.Slot.Index).ToArray();
            if (ready.Length == 0) continue;
            double distance = ready.Min(i => walkways.Min(w => IndustryClusterPlanner.Distance(w, chests[row.Id][i])));
            if (distance <= SupplyLinePlanner.MinimumDistance) continue;
            var line = Line(state, row.Id);
            string? reason = line switch
            {
                null => "new",
                { Status: "building" } => "resume",
                { Status: "ready", Plan: { } plan } when ready.Any(i => plan.ContainsKey(SupplyLinePlanner.FeederRole(i))
                    && !line.Entities.ContainsKey(SupplyLinePlanner.FeederRole(i))) => "extend",
                _ => null
            };
            if (reason is not null) needs.Add(new(row.Id, row.Product, reason, Math.Round(distance, 2), ready.Length, line?.Id));
        }
        return needs.OrderBy(n => n.Reason switch { "resume" => 0, "extend" => 1, _ => 2 })
            .ThenByDescending(n => n.ReadyCells * n.Distance).ThenBy(n => n.Row).FirstOrDefault();
    }
}
