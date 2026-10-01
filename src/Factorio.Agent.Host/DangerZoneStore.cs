using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Durable C# memory of the actor's recent deaths in one world, beside the factory registry.</summary>
public sealed class DangerZoneStore(string directory)
{
    public string Path => System.IO.Path.Combine(directory, "danger-zones.json");

    public async Task<DangerZones> LoadAsync(string worldId, CancellationToken token)
    {
        if (!File.Exists(Path)) return DangerZones.Empty(worldId);
        var zones = JsonSerializer.Deserialize<DangerZones>(await File.ReadAllTextAsync(Path, token), Protocol.Json)
            ?? throw new InvalidDataException("Empty danger zone memory.");
        zones.Validate(worldId);
        return zones;
    }

    /// <summary>Idempotent: an already remembered death leaves the file untouched.</summary>
    public async Task<DangerZones> RecordAsync(string worldId, NativeDeathTransition death, CancellationToken token)
    {
        var previous = await LoadAsync(worldId, token);
        var updated = previous.Record(death);
        if (!ReferenceEquals(updated, previous)) await LocalJson.WriteAsync(Path, updated, token);
        return updated;
    }
}
