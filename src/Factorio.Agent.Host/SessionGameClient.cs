using System.Diagnostics;
using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Serializes calls across CLI processes and detects observed world-history regressions.</summary>
public sealed class SessionGameClient(RuntimeSession session, IGameClient inner, ActorControlLease? controllerLease = null) : IGameClient, IResourceMemoryReader, IAsyncDisposable
{
    private static readonly HashSet<string> Mutations = ["hello", "submit", "cancel", "mark_fixture", "prepare_checkpoint"];
    private readonly SemaphoreSlim callGate = new(1, 1);
    private int controlWaiters;
    private bool disposed;
    private string WatermarkPath => Path.Combine(session.Directory, "observation-watermark.json");

    public async Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
    {
        using IDisposable priority = await AcquireCallAsync(request.Action != "factory_snapshot", cancellationToken);
        await using FileStream guard = await AcquireLockAsync(cancellationToken);
        using ActorControlLease? callLease = Mutations.Contains(request.Action) && controllerLease is null
            ? ActorControlLease.Acquire(session.Directory) : null;
        if (Mutations.Contains(request.Action)) controllerLease?.Validate(session.Directory);
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
        if (request.Action == "spatial" && response.Ok)
            await new ResourceMemoryStore(session.Directory).RecordAsync(SpatialSnapshot.Parse(response), cancellationToken);
        return response;
    }

    public async Task<ResourceMemorySnapshot> ReadResourceMemoryAsync(SpatialSnapshot current, CancellationToken token = default)
    {
        if (current.Scope.WorldId != session.ProposedWorldId || current.Scope.SessionId != session.SessionId)
            throw new SessionDivergenceException("Resource memory request belongs to another world or controller session.");
        using IDisposable priority = await AcquireCallAsync(control: false, token);
        await using FileStream guard = await AcquireLockAsync(token);
        return await new ResourceMemoryStore(session.Directory).ReadAsync(current, token);
    }

    public async Task<ResourceMemorySnapshot> ImportResourceHistoryAsync(SpatialSnapshot current, IReadOnlyList<ResourceSighting> history, CancellationToken token)
    {
        if (controllerLease is null) throw new InvalidOperationException("Historical import requires exclusive actor control.");
        controllerLease.Validate(session.Directory);
        if (current.Scope.WorldId != session.ProposedWorldId || current.Scope.SessionId != session.SessionId)
            throw new SessionDivergenceException("Historical import belongs to another world or controller session.");
        using IDisposable priority = await AcquireCallAsync(control: false, token);
        await using FileStream guard = await AcquireLockAsync(token);
        return await new ResourceMemoryStore(session.Directory).ImportAsync(current, history, token);
    }

    // A stock page cannot interrupt a call already in flight, but it yields the
    // next slot to queued control/observation calls in this controller instance.
    private async Task<IDisposable> AcquireCallAsync(bool control, CancellationToken token)
    {
        if (control) Interlocked.Increment(ref controlWaiters);
        try
        {
            while (true)
            {
                await callGate.WaitAsync(token);
                if (disposed) { callGate.Release(); throw new ObjectDisposedException(nameof(SessionGameClient)); }
                if (control || Volatile.Read(ref controlWaiters) == 0) return new CallLease(callGate);
                callGate.Release();
                await Task.Delay(1, token);
            }
        }
        finally
        {
            if (control) Interlocked.Decrement(ref controlWaiters);
        }
    }

    private sealed class CallLease(SemaphoreSlim gate) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await callGate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            if (inner is IAsyncDisposable owned) await owned.DisposeAsync();
        }
        finally { callGate.Release(); }
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

    private Task PersistAsync(Watermark value, CancellationToken token) => LocalJson.WriteAsync(WatermarkPath, value, token);

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
