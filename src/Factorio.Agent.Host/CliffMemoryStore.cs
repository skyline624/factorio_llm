using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Durable memory of the cliff segments the actor has seen in one world, beside the nest memory.</summary>
public sealed class CliffMemoryStore(string directory)
{
    public string Path => System.IO.Path.Combine(directory, "cliff-memory.json");

    public async Task<CliffMemory> LoadAsync(string worldId, CancellationToken token)
    {
        if (!File.Exists(Path)) return CliffMemory.Empty(worldId);
        var memory = JsonSerializer.Deserialize<CliffMemory>(await File.ReadAllTextAsync(Path, token), Protocol.Json)
            ?? throw new InvalidDataException("Empty cliff memory.");
        if (memory.Version != 1 || memory.WorldId != worldId) throw new InvalidDataException("Cliff memory belongs to another world or format.");
        return memory;
    }

    /// <summary>Writes only when the photograph added or removed a remembered cliff.</summary>
    public async Task<CliffMemory> RecordAsync(SpatialSnapshot map, CancellationToken token)
    {
        var previous = await LoadAsync(map.Scope.WorldId, token);
        var updated = previous.Observe(map);
        if (updated.Cliffs.Count != previous.Cliffs.Count || updated.Cliffs.Keys.Any(k => !previous.Cliffs.ContainsKey(k)))
            await LocalJson.WriteAsync(Path, updated, token);
        return updated;
    }
}
