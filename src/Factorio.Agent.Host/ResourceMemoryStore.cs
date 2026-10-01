using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Called under the session's cross-process lock; atomic replacement survives CLI restarts.</summary>
internal sealed class ResourceMemoryStore(string directory)
{
    private string FilePath(string worldId, int surfaceIndex)
    {
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(worldId + ":" + surfaceIndex)));
        return Path.Combine(directory, "resource-memory-" + key + ".json");
    }
    public Task<ResourceMemorySnapshot> ReadAsync(SpatialSnapshot map, CancellationToken token) =>
        ReadAsync(map.Scope.WorldId, map.SurfaceIndex, map.CollectedTick, token);

    private async Task<ResourceMemorySnapshot> ReadAsync(string worldId, int surfaceIndex, long tick, CancellationToken token)
    {
        string path = FilePath(worldId, surfaceIndex);
        var memory = File.Exists(path)
            ? JsonSerializer.Deserialize<ResourceMemorySnapshot>(await File.ReadAllTextAsync(path, token), Protocol.Json)
                ?? throw new InvalidDataException("Empty resource memory file.")
            : ResourceMemorySnapshot.Empty(worldId, surfaceIndex, tick);
        memory.ValidateFor(worldId, surfaceIndex, tick);
        return memory;
    }
    public async Task<ResourceMemorySnapshot> ImportAsync(SpatialSnapshot map, IReadOnlyList<ResourceSighting> history, CancellationToken token)
    {
        var prior = await ReadAsync(map, token);
        var combined = prior.Resources.Concat(history).GroupBy(r => r.EntityId)
            .Select(g => g.OrderByDescending(r => r.ObservedTick).First()).OrderByDescending(r => r.ObservedTick).ToArray();
        var updated = (prior with { LastTick = map.CollectedTick, Resources = combined.Take(8192).ToArray(),
            Truncated = prior.Truncated || combined.Length > 8192 }).Merge(map);
        await LocalJson.WriteAsync(FilePath(map.Scope.WorldId, map.SurfaceIndex), updated, token);
        return updated;
    }
    public async Task RecordAsync(SpatialSnapshot map, CancellationToken token)
    {
        var previous = await ReadAsync(map, token);
        await LocalJson.WriteAsync(FilePath(map.Scope.WorldId, map.SurfaceIndex), previous.Merge(map), token);
    }
    public async Task RecordChartedAsync(ChartedResourceSnapshot charted, CancellationToken token)
    {
        var previous = await ReadAsync(charted.Scope.WorldId, charted.SurfaceIndex, charted.CollectedTick, token);
        await LocalJson.WriteAsync(FilePath(charted.Scope.WorldId, charted.SurfaceIndex), previous.MergeCharted(charted), token);
    }
}
