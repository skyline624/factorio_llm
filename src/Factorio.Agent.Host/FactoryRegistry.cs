using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

public sealed record FactoryZone(int Id, MapPosition Origin, int Slots, int Pitch, int BandHeight);
/// <summary>
/// A persistent automated cell. Entity identities are native ids proven by build receipts. Attempts counts the builds
/// of a resource cell since it last stood complete, so a cell that keeps failing is abandoned instead of resumed forever.
/// </summary>
public sealed record FactoryCell(string Id, int Zone, CellSlot Slot, string Kind, string MachineItem, string? Recipe,
    IReadOnlyDictionary<string, string> Entities, string Status, long Tick, int Attempts = 0)
{
    /// <summary>Resource, power and defense cells all live outside bands (zone 0); only the kind tells them apart.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsResource => Zone == 0 && Kind is "smelter" or "miner";
}
/// <summary>Resource cells use zone 0; their slot band is the id of their <see cref="ResourceRow"/>.</summary>
public sealed record FactoryState(int Version, string WorldId, IReadOnlyList<FactoryZone> Zones, IReadOnlyList<FactoryCell> Cells,
    IReadOnlyList<ResourceRow>? Rows = null)
{
    public FactoryState With(FactoryCell cell) => this with { Cells = [.. Cells.Where(c => c.Id != cell.Id), cell] };
    public FactoryState With(ResourceRow row) => this with { Rows = [.. (Rows ?? []).Where(r => r.Id != row.Id), row] };
}

/// <summary>Durable C# memory of the automated factory in one world. The engine stays the source of stock truth.</summary>
public sealed class FactoryRegistry(string directory)
{
    public string Path => System.IO.Path.Combine(directory, "factory-cells.json");

    public async Task<FactoryState> LoadAsync(string worldId, CancellationToken token)
    {
        if (!File.Exists(Path)) return new(1, worldId, [], []);
        var state = JsonSerializer.Deserialize<FactoryState>(await File.ReadAllTextAsync(Path, token), Protocol.Json)
            ?? throw new InvalidDataException("Empty factory registry.");
        if (state.Version != 1 || state.WorldId != worldId) throw new InvalidDataException("The factory registry belongs to another world.");
        return state;
    }

    public Task SaveAsync(FactoryState state, CancellationToken token) => LocalJson.WriteAsync(Path, state, token);

    /// <summary>
    /// Entities that actor-driven production must not reuse: a cell assembler would fight its inserters and its
    /// chests feed other cells. Callers enter ProductionReservations themselves because an AsyncLocal value set
    /// inside an async method does not flow back to the caller.
    /// </summary>
    public async Task<IReadOnlySet<string>> CellEntityIdsAsync(string worldId, CancellationToken token) =>
        (await LoadAsync(worldId, token)).Cells.SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal);

    /// <summary>Allocates the next slot of a compatible zone: north then south row, from the zone origin outward.</summary>
    public static CellSlot? NextSlot(FactoryState state, FactoryZone zone)
    {
        var used = state.Cells.Where(c => c.Zone == zone.Id).Select(c => (c.Slot.Index, c.Slot.North)).ToHashSet();
        for (int index = 0; index < zone.Slots; index++)
            foreach (bool north in new[] { true, false })
                if (!used.Contains((index, north))) return new(0, index, north);
        return null;
    }
}
