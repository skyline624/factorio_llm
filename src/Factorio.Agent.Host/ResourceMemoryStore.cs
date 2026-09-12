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
    public async Task RecordAsync(SpatialSnapshot map, CancellationToken token)
    {
        var previous = await ReadAsync(map, token);
        await LocalJson.WriteAsync(FilePath(map), previous.Merge(map), token);
    }
}
