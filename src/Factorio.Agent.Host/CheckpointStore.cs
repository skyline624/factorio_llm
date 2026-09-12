using System.Security.Cryptography;
using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

public sealed record CheckpointSeal(string CheckpointId, ActorScope Scope, long Tick, string Sha256, DateTime RecordedUtc);

public static class LocalJson
{
    public static async Task WriteAsync<T>(string path, T value, CancellationToken token = default)
    {
        string temporary = path + ".pending";
        await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(file, value, Protocol.Json, token);
            await file.FlushAsync(token);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}

public static class CheckpointStore
{
    public static string SavePath(RuntimeSession session) => Path.Combine(session.Directory, "saves", "agent-checkpoint.zip");
    public static string SealPath(RuntimeSession session) => Path.Combine(session.Directory, "checkpoint.json");

    public static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (input.Length == 0) throw new InvalidDataException("Checkpoint is empty.");
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
    }

    public static async Task<CheckpointSeal?> VerifyFileAsync(RuntimeSession session, CancellationToken token)
    {
        string hash = await HashAsync(SavePath(session), token);
        if (!File.Exists(SealPath(session))) return null; // Legacy checkpoints require a read-only engine inspection.
        CheckpointSeal seal = JsonSerializer.Deserialize<CheckpointSeal>(await File.ReadAllTextAsync(SealPath(session), token), Protocol.Json)
            ?? throw new InvalidDataException("Missing checkpoint seal.");
        if (seal.Sha256 != hash || seal.Scope.WorldId != session.ProposedWorldId || string.IsNullOrWhiteSpace(seal.CheckpointId))
            throw new SessionDivergenceException("Checkpoint contents or world do not match the recorded seal.");
        string watermarkFile = Path.Combine(session.Directory, "observation-watermark.json");
        if (File.Exists(watermarkFile))
        {
            var watermark = JsonSerializer.Deserialize<SessionGameClient.Watermark>(await File.ReadAllTextAsync(watermarkFile, token), Protocol.Json)!;
            if (watermark.WorldId != seal.Scope.WorldId || watermark.Tick > seal.Tick
                || watermark.Incarnation > seal.Scope.Incarnation || watermark.Generation > seal.Scope.Generation)
                throw new SessionDivergenceException("The checkpoint predates known progress; do not restore it as a normal continuation.");
        }
        return seal;
    }

    public static ActorScope VerifyLoaded(GameResponse observation, RuntimeSession prior, CheckpointSeal? seal,
        SessionGameClient.Watermark? watermark)
    {
        if (!observation.Ok) throw new GameRpcException(observation.Error!);
        ActorScope scope = observation.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
        string expectedSession = seal?.Scope.SessionId ?? prior.SessionId;
        if (scope.WorldId != prior.ProposedWorldId || scope.SessionId != expectedSession
            || (watermark is not null && (watermark.WorldId != scope.WorldId || observation.Tick < watermark.Tick || scope.Incarnation < watermark.Incarnation
                || scope.Generation < watermark.Generation)))
            throw new SessionDivergenceException("Loaded checkpoint does not continue the recorded world and actor history.");
        JsonElement agent = observation.Data.GetProperty("agent");
        if (seal is not null && (observation.Tick != seal.Tick || scope != seal.Scope
            || !agent.GetProperty("awaitingController").GetBoolean()
            || agent.GetProperty("checkpointId").GetString() != seal.CheckpointId))
            throw new SessionDivergenceException("The loaded engine does not match the prepared checkpoint.");
        if (observation.Data.TryGetProperty("operation", out JsonElement operation) && operation.ValueKind == JsonValueKind.Object
            && operation.TryGetProperty("operationId", out var id) && !OperationReceipt.Parse(operation, id.GetString()!).IsTerminal)
            throw new SessionDivergenceException("A legacy checkpoint contains active work; automatic replay is refused.");
        return scope;
    }
}
