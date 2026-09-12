using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Called under the session's cross-process lock; atomic replacement survives CLI restarts.</summary>
internal sealed class ResourceMemoryStore(string directory)
{
    private string FilePath(SpatialSnapshot map)
    {
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(map.Scope.WorldId + ":" + map.SurfaceIndex)));
        return Path.Combine(directory, "resource-memory-" + key + ".json");
    }
    public async Task<ResourceMemorySnapshot> ReadAsync(SpatialSnapshot map, CancellationToken token)
    {
        string path = FilePath(map);
        var memory = File.Exists(path)
            ? JsonSerializer.Deserialize<ResourceMemorySnapshot>(await File.ReadAllTextAsync(path, token), Protocol.Json)
                ?? throw new InvalidDataException("Empty resource memory file.")
            : ResourceMemorySnapshot.Empty(map);
        memory.ValidateFor(map);
        return memory;
    }
    public async Task<ResourceMemorySnapshot> ImportAsync(SpatialSnapshot map, IReadOnlyList<ResourceSighting> history, CancellationToken token)
    {
        var prior = await ReadAsync(map, token);
        var combined = prior.Resources.Concat(history).GroupBy(r => r.EntityId)
            .Select(g => g.OrderByDescending(r => r.ObservedTick).First()).OrderByDescending(r => r.ObservedTick).ToArray();
        var updated = (prior with { LastTick = map.CollectedTick, Resources = combined.Take(8192).ToArray(),
            Truncated = prior.Truncated || combined.Length > 8192 }).Merge(map);
        await LocalJson.WriteAsync(FilePath(map), updated, token);
        return updated;
    }
    public async Task RecordAsync(SpatialSnapshot map, CancellationToken token)
    {
        var previous = await ReadAsync(map, token);
        await LocalJson.WriteAsync(FilePath(map), previous.Merge(map), token);
    }
}
