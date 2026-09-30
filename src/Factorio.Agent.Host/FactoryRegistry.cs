using System.Text.Json;
using System.Text.Json.Serialization;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

public sealed record FactoryZone(int Id, MapPosition Origin, int Slots, int Pitch, int BandHeight)
{
    /// <summary>The whole band, including slots not built yet.</summary>
    [JsonIgnore] public WorldBox Box => new(Origin, new(Origin.X + Slots * Pitch, Origin.Y + BandHeight));
}
/// <summary>
/// A persistent automated cell. Entity identities are native ids proven by build receipts. Plan records each role's
/// item, position and direction so maintenance can rebuild it; registries written before plans load without them.
/// </summary>
public sealed record FactoryCell(string Id, int Zone, CellSlot Slot, string Kind, string MachineItem, string? Recipe,
    IReadOnlyDictionary<string, string> Entities, string Status, long Tick, IReadOnlyDictionary<string, PlannedEntity>? Plan = null);
public sealed record FactoryState(int Version, string WorldId, IReadOnlyList<FactoryZone> Zones, IReadOnlyList<FactoryCell> Cells)
{
    public FactoryState With(FactoryCell cell) => this with { Cells = [.. Cells.Where(c => c.Id != cell.Id), cell] };
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
