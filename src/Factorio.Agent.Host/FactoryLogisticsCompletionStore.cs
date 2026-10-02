using System.Security.Cryptography;
using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Optional scheduling evidence; no inventory or operation receipt is inferred from this record.</summary>
internal sealed class FactoryLogisticsCompletionStore(string directory)
{
    private sealed record Completion(ActorScope Scope, long Tick, string FactoryFingerprint, int BufferCrafts, bool FuelShort);
    private string Path => System.IO.Path.Combine(directory, "factory-logistics-completion.json");

    public async Task<bool> IsFreshAsync(ActorScope scope, long tick, FactoryState state, int bufferCrafts,
        long maximumAgeTicks, CancellationToken token)
    {
        try
        {
            if (!File.Exists(Path)) return false;
            var completion = JsonSerializer.Deserialize<Completion>(await File.ReadAllTextAsync(Path, token), Protocol.Json);
            return completion is not null && completion.Scope == scope && !completion.FuelShort
                && completion.BufferCrafts == bufferCrafts && completion.Tick <= tick && tick - completion.Tick <= maximumAgeTicks
                && completion.FactoryFingerprint == Fingerprint(state);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return false; // Missing or unusable optimization evidence always schedules the normal tour.
        }
    }

    public Task RecordAsync(ActorScope scope, long tick, FactoryState state, int bufferCrafts, bool fuelShort, CancellationToken token) =>
        LocalJson.WriteAsync(Path, new Completion(scope, tick, Fingerprint(state), bufferCrafts, fuelShort), token);

    private static string Fingerprint(FactoryState state) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state, Protocol.Json)));
}
