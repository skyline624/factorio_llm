using System.Diagnostics;
using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Serializes calls across CLI processes and detects observed world-history regressions.</summary>
public sealed class SessionGameClient(RuntimeSession session, IGameClient inner) : IGameClient
{
    private static readonly HashSet<string> Mutations = ["hello", "submit", "cancel", "mark_fixture"];
    private string WatermarkPath => Path.Combine(session.Directory, "observation-watermark.json");

    public async Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
    {
        await using FileStream guard = await AcquireLockAsync(cancellationToken);
        Watermark? previous = File.Exists(WatermarkPath)
            ? JsonSerializer.Deserialize<Watermark>(await File.ReadAllTextAsync(WatermarkPath, cancellationToken), Protocol.Json)
            : null;
        // A stale save must be rejected before the submitted mutation can reach the engine.
        if (previous is not null && Mutations.Contains(request.Action))
        {
            GameResponse observed = await inner.ExecuteAsync(GameRequest.Create("observe", new { radius = 1, limit = 1 }), cancellationToken);
            if (!observed.Ok) throw new GameRpcException(observed.Error!);
            previous = Verify(observed, previous);
            await PersistAsync(previous, cancellationToken);
        }
        GameResponse response = await inner.ExecuteAsync(request, cancellationToken);
        if (response.Ok || previous is not null)
        {
            Watermark verified = Verify(response, previous);
            await PersistAsync(verified, cancellationToken);
        }
        return response;
    }

    private Watermark Verify(GameResponse response, Watermark? previous)
    {
        ActorScope? scope = response.Data.ValueKind == JsonValueKind.Object && response.Data.TryGetProperty("scope", out JsonElement node)
            ? node.Deserialize<ActorScope>(Protocol.Json) : null;
        if (previous is not null && response.Tick < previous.Tick)
            throw new SessionDivergenceException("The game tick regressed. Reconcile the restored save and outstanding operations before resuming.");
        if (scope is not null && (scope.WorldId != session.ProposedWorldId || scope.SessionId != session.SessionId))
            throw new SessionDivergenceException("The engine world/controller session differs from this manifest.");
        if (scope is not null && previous is not null && (scope.Incarnation < previous.Incarnation || scope.Generation < previous.Generation))
            throw new SessionDivergenceException("The actor incarnation or control generation regressed.");
        return new(session.ProposedWorldId, session.SessionId, response.Tick,
            scope?.Incarnation ?? previous?.Incarnation ?? 0, scope?.Generation ?? previous?.Generation ?? 0);
    }

    private async Task PersistAsync(Watermark value, CancellationToken token)
    {
        string temporary = WatermarkPath + ".pending";
        await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(file, value, Protocol.Json, token);
            await file.FlushAsync(token);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, WatermarkPath, overwrite: true);
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken token)
    {
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(Path.Combine(session.Directory, "controller.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(20))
            {
                await Task.Delay(50, token);
            }
        }
    }

    public sealed record Watermark(string WorldId, string SessionId, long Tick, long Incarnation, long Generation);
}

public sealed class SessionDivergenceException(string message) : IOException(message);
