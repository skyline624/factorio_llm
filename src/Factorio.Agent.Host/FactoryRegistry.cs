using System.Text.Json;
using System.Text.Json.Serialization;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

public sealed record FactoryZone(int Id, MapPosition Origin, int Slots, int Pitch, int BandHeight)
{
    /// <summary>The whole band, including slots not built yet.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public WorldBox Box => new(Origin, new(Origin.X + Slots * Pitch, Origin.Y + BandHeight));
}
/// <summary>
/// A persistent automated cell. Entity identities are native ids proven by build receipts. Attempts counts the builds
/// of a resource cell since it last stood complete, so a cell that keeps failing is abandoned instead of resumed forever.
/// Plan records each role's item, position and direction so maintenance can rebuild it; older registries load without it.
/// </summary>
public sealed record FactoryCell(string Id, int Zone, CellSlot Slot, string Kind, string MachineItem, string? Recipe,
    IReadOnlyDictionary<string, string> Entities, string Status, long Tick, int Attempts = 0,
    IReadOnlyDictionary<string, PlannedEntity>? Plan = null)
{
    /// <summary>Resource, power and defense cells all live outside bands (zone 0); only the kind tells them apart.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsResource => Zone == 0 && Kind is "smelter" or "miner";
}
public sealed record FactoryTransportConsumer(string TargetCellId, string InserterRole, int Maximum, bool Paused = false);
/// <summary>A single-item belt bus owns its transport cell; endpoints refer to producer/consumer cells so rebuilt chests are found by their roles.</summary>
public sealed record FactoryTransportBus(string Id, string SourceCellId, string Item, string CellId,
    IReadOnlyList<FactoryTransportConsumer> Consumers, int? ActorReserve = null);
/// <summary>Resource cells use zone 0; their slot band is the id of their <see cref="ResourceRow"/>.</summary>
/// <summary>Targets are the automation rates requested so far, item to items per minute; older registries load without them.</summary>
public sealed record FactoryState(int Version, string WorldId, IReadOnlyList<FactoryZone> Zones, IReadOnlyList<FactoryCell> Cells,
    IReadOnlyList<ResourceRow>? Rows = null, IReadOnlyDictionary<string, double>? Targets = null,
    IReadOnlyList<FactoryTransportBus>? Transports = null)
{
    public FactoryState With(FactoryCell cell) => this with { Cells = [.. Cells.Where(c => c.Id != cell.Id), cell] };
    public FactoryState With(ResourceRow row) => this with { Rows = [.. (Rows ?? []).Where(r => r.Id != row.Id), row] };
    public FactoryState With(FactoryTransportBus bus) => this with { Transports = [.. (Transports ?? []).Where(b => b.Id != bus.Id), bus] };

    /// <summary>A target keeps the highest rate ever requested: the factory grows with demand and never shrinks a plan.</summary>
    public FactoryState WithTarget(string item, double perMinute) => this with
    {
        Targets = new Dictionary<string, double>(Targets ?? new Dictionary<string, double>(), StringComparer.Ordinal)
        {
            [item] = Math.Max(Targets?.GetValueOrDefault(item) ?? 0, perMinute)
        }
    };
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
