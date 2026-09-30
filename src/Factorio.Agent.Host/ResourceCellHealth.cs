using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>
/// Native health of resource cells from one factory photograph, which lists every known own entity wherever it stands.
/// A destroyed part reopens its cell for repair without that part; a drill whose native status reports no minable
/// resources retires its cell as depleted. Power loss changes nothing here: a whole network can brown out, and cells
/// that stop delivering already hand their product back to procurement.
/// </summary>
public static class ResourceCellHealth
{
    public const string Depleted = "depleted";

    /// <summary>Ready resource cells whose status changes, with the parts that still stand.</summary>
    public static IReadOnlyList<FactoryCell> Inspect(FactorySnapshot snapshot, IEnumerable<FactoryCell> cells)
    {
        var changed = new List<FactoryCell>();
        foreach (var cell in cells.Where(c => c.Zone == 0 && c.Status == "ready"))
        {
            var standing = Standing(snapshot, cell.Entities);
            if (standing.Count < cell.Entities.Count) changed.Add(cell with { Status = "building", Entities = standing, Attempts = 0 });
            else if (cell.Entities.TryGetValue("drill", out string? drill) && DrillStatus(snapshot, drill) == "no_minable_resources")
                changed.Add(cell with { Status = Depleted });
        }
        return changed;
    }

    /// <summary>Recorded roles whose native entity still stands, at its planned position when a layout is given.</summary>
    public static IReadOnlyDictionary<string, string> Standing(FactorySnapshot snapshot, IReadOnlyDictionary<string, string> recorded,
        CellLayout? layout = null)
    {
        var entities = snapshot.Records.Where(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "factory")
            .ToDictionary(r => r.EntityId, StringComparer.Ordinal);
        return recorded.Where(p => entities.TryGetValue(p.Value, out var record) && (layout?.Role(p.Key) is not { } planned
                || record.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!.DistanceTo(planned.Position) < .01))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    private static string? DrillStatus(FactorySnapshot snapshot, string drillId) =>
        snapshot.Records.SingleOrDefault(r => r.Kind == "work" && r.EntityId == drillId) is { } work
        && work.Data.TryGetProperty("statusName", out var status) ? status.GetString() : null;
}
