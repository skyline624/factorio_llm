using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Infrastructure.Tests;

public sealed class FactorySnapshotRetryTests
{
    private static readonly ActorScope Scope = new("world", "session", "actor", 1, 2);

    [Fact]
    public async Task Evicted_read_restarts_from_zero_and_discards_all_previous_records()
    {
        var game = new EvictingGame();
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(["iron-plate"], pageSize: 1);
        Assert.Equal("snapshot-2", snapshot.SnapshotId);
        Assert.Equal(7, snapshot.SummarizeStocks().InventoryItems["iron-plate"]);
        Assert.Equal(2, snapshot.SummarizeStocks().TransitItems["iron-plate"]);
        Assert.Equal(4, game.Calls.Count);
        var starts = game.Calls.Where(r => !r.Arguments.TryGetProperty("snapshotId", out _)).ToArray();
        Assert.Equal(2, starts.Length);
        Assert.All(starts, r => Assert.Equal("iron-plate", r.Arguments.GetProperty("capacityItems")[0].GetString()));
        Assert.All(game.Calls, r => Assert.Equal("factory_snapshot", r.Action));
    }

    [Fact]
    public async Task Two_evictions_are_recovered_without_combining_the_three_captures()
    {
        var game = new EvictingGame { Expirations = 2 };
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(pageSize: 1);
        Assert.Equal("snapshot-3", snapshot.SnapshotId);
        Assert.Equal(7, snapshot.SummarizeStocks().InventoryItems["iron-plate"]);
        Assert.Equal(6, game.Calls.Count);
    }

    [Fact]
    public async Task Repeated_eviction_stops_after_three_complete_capture_attempts()
    {
        var game = new EvictingGame { Expirations = int.MaxValue };
        var error = await Assert.ThrowsAsync<GameRpcException>(() => new FactorySnapshotClient(game).CaptureAsync(pageSize: 1));
        Assert.Equal("snapshot_expired", error.Error.Code);
        Assert.Equal(6, game.Calls.Count);
    }

    [Fact]
    public async Task A_changed_actor_scope_after_eviction_is_not_accepted()
    {
        var game = new EvictingGame { ChangeScope = true };
        await Assert.ThrowsAsync<InvalidDataException>(() => new FactorySnapshotClient(game).CaptureAsync(pageSize: 1));
        Assert.Equal(3, game.Calls.Count);
    }

    [Fact]
    public async Task A_recaptured_snapshot_cannot_predate_the_evicted_read()
    {
        var game = new EvictingGame { RegressTick = true };
        await Assert.ThrowsAsync<InvalidDataException>(() => new FactorySnapshotClient(game).CaptureAsync(pageSize: 1));
        Assert.Equal(3, game.Calls.Count);
    }

    [Fact]
    public async Task A_different_native_error_is_not_retried()
    {
        var game = new EvictingGame { ErrorCode = "handshake_required" };
        var error = await Assert.ThrowsAsync<GameRpcException>(() => new FactorySnapshotClient(game).CaptureAsync(pageSize: 1));
        Assert.Equal("handshake_required", error.Error.Code);
        Assert.Equal(2, game.Calls.Count);
    }

    [Fact]
    public async Task Cancellation_at_eviction_prevents_another_capture()
    {
        using var cancellation = new CancellationTokenSource();
        var game = new EvictingGame { CancelAtEviction = cancellation };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FactorySnapshotClient(game)
            .CaptureAsync(pageSize: 1, cancellationToken: cancellation.Token));
        Assert.Equal(2, game.Calls.Count);
    }

    private sealed class EvictingGame : IGameClient
    {
        public int Expirations { get; init; } = 1;
        public string ErrorCode { get; init; } = "snapshot_expired";
        public bool ChangeScope { get; init; }
        public bool RegressTick { get; init; }
        public CancellationTokenSource? CancelAtEviction { get; init; }
        public List<GameRequest> Calls { get; } = [];
        private int captures;

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(request);
            bool start = !request.Arguments.TryGetProperty("snapshotId", out _);
            if (start) captures++;
            if (!start && captures <= Expirations)
            {
                CancelAtEviction?.Cancel();
                return Task.FromResult(new GameResponse(1, request.RequestId, false, captures * 100 + 1,
                    default, new(ErrorCode, "Synthetic native read failure")));
            }
            int offset = start ? 0 : request.Arguments.GetProperty("offset").GetInt32();
            ActorScope scope = ChangeScope && captures > 1 ? Scope with { Generation = Scope.Generation + 1 } : Scope;
            FactoryRecord record = offset == 0
                ? new("stock", "inventory", "owner", "inventory", Protocol.ToElement(new { items = new Dictionary<string, int> { ["iron-plate"] = captures <= Expirations ? 99 : 7 } }))
                : new("transit", "transit", "belt", "transport-line", Protocol.ToElement(new { items = new Dictionary<string, int> { ["iron-plate"] = 2 } }));
            long collectedTick = RegressTick && captures > 1 ? 99 : captures * 100;
            var data = new FactorySnapshotPage("snapshot-" + captures, scope, scope, collectedTick, collectedTick + 3600,
                2, offset, offset + 1, offset == 1, Protocol.ToElement(new { atomic = true, knownInventoriesComplete = true,
                    knownBeltAndInserterTransitComplete = true, fluidSegmentsDeduplicated = true }), [record]);
            return Task.FromResult(new GameResponse(1, request.RequestId, true, captures * 100 + offset, Protocol.ToElement(data)));
        }
    }
}
