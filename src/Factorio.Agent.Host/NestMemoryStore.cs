using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Durable C# memory of the enemy structures the actor has seen in one world, beside the factory registry.</summary>
public sealed class NestMemoryStore(string directory)
{
    public string Path => System.IO.Path.Combine(directory, "nest-memory.json");

    public async Task<NestMemory> LoadAsync(string worldId, CancellationToken token)
    {
        if (!File.Exists(Path)) return NestMemory.Empty(worldId);
        var memory = JsonSerializer.Deserialize<NestMemory>(await File.ReadAllTextAsync(Path, token), Protocol.Json)
            ?? throw new InvalidDataException("Empty nest memory.");
        if (memory.Version != 1 || memory.WorldId != worldId) throw new InvalidDataException("Nest memory belongs to another world or format.");
        return memory;
    }

    /// <summary>Writes only when the photograph added, moved or removed a remembered structure.</summary>
    public async Task<NestMemory> RecordAsync(SpatialSnapshot map, CancellationToken token)
    {
        var previous = await LoadAsync(map.Scope.WorldId, token);
        var updated = previous.Observe(map);
        bool changed = previous.Structures.Count != updated.Structures.Count || previous.Structures.Zip(updated.Structures)
            .Any(p => p.First.Id != p.Second.Id || p.First.Position != p.Second.Position);
        if (changed) await LocalJson.WriteAsync(Path, updated, token);
        return updated;
    }
}
