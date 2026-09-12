using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Infrastructure.Tests;

public sealed class FactorySnapshotTests
{
    private static readonly ActorScope Scope = new("world", "session", "actor", 1, 2);

    [Fact]
    public async Task Pages_form_one_snapshot_and_keep_storage_transit_and_fluids_separate()
    {
        var game = new PagedGame();
        FactorySnapshot snapshot = await new FactorySnapshotClient(game).CaptureAsync(["iron-plate"], pageSize: 2);
        FactoryStockSummary stocks = snapshot.SummarizeStocks();
        Assert.Equal(15, stocks.InventoryItems["iron-plate"]);
        Assert.Equal(3, stocks.TransitItems["iron-plate"]);
        Assert.Equal(123.75, stocks.Fluids["water"]);
        Assert.Equal(2, game.Calls.Count);
        Assert.Equal(2, game.Calls[1].Arguments.GetProperty("offset").GetInt32());
        Assert.False(game.Calls[1].Arguments.TryGetProperty("capacityItems", out _));
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("tick")]
    [InlineData("scope")]
    [InlineData("offset")]
    [InlineData("duplicate")]
    [InlineData("truncated")]
    [InlineData("expired")]
    [InlineData("coverage")]
    public async Task Mixed_missing_stale_or_repeated_records_are_refused(string fault)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new FactorySnapshotClient(new PagedGame { Fault = fault })
            .CaptureAsync(pageSize: 2));
    }

    [Fact]
    public async Task Native_empty_collection_is_accepted_without_inventing_stock()
    {
        FactorySnapshot snapshot = await new FactorySnapshotClient(new PagedGame { Empty = true }).CaptureAsync();
        Assert.Empty(snapshot.Records);
        Assert.Empty(snapshot.SummarizeStocks().InventoryItems);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("unsafe-fluid")]
    public async Task Invalid_stock_values_are_not_accepted_as_a_complete_snapshot(string fault)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new FactorySnapshotClient(new PagedGame { Fault = fault })
            .CaptureAsync(pageSize: 2));
    }

    private sealed class PagedGame : IGameClient
    {
        public string? Fault { get; init; }
        public bool Empty { get; init; }
        public List<GameRequest> Calls { get; } = [];
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            int offset = request.Arguments.TryGetProperty("offset", out JsonElement value) ? value.GetInt32() : 0;
            bool second = offset > 0;
            FactoryRecord[] records =
            [
                Record("a", "inventory", new { items = new Dictionary<string, long> { ["iron-plate"] = Fault == "negative" ? -15 : 15 } }),
                Record("b", "transit", new { items = new Dictionary<string, int> { ["iron-plate"] = 3 } }),
                Record("c", "fluid", new { contents = new { water = 123.75 }, aggregateSafe = Fault != "unsafe-fluid" }),
                Record("d", "work", new { recipe = "iron-plate", ingredients = new { ironOre = 1 }, progress = 0.5 })
            ];
            object pageRecords = Empty ? new { } : records.Skip(offset).Take(2).ToArray();
            if (second && Fault == "duplicate") pageRecords = new[] { records[0], records[3] };
            if (second && Fault == "truncated") pageRecords = new { };
            object data = new
            {
                snapshotId = second && Fault == "snapshot" ? "other" : "snapshot",
                scope = second && Fault == "scope" ? Scope with { Generation = 3 } : Scope, snapshotScope = Scope,
                collectedTick = second && Fault == "tick" ? 101 : 100,
                expiresTick = second && Fault == "expired" ? 101 : 3700, totalRecords = Empty ? 0 : 4,
                offset = second && Fault == "offset" ? 1 : offset,
                nextOffset = Empty ? 0 : offset + 2, complete = Empty || second,
                coverage = new { atomic = true, knownInventoriesComplete = !(second && Fault == "coverage"),
                    knownBeltAndInserterTransitComplete = true, fluidSegmentsDeduplicated = true },
                records = pageRecords
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, second ? 110 : 100, Protocol.ToElement(data)));
        }

        private static FactoryRecord Record(string id, string kind, object data) => new(id, kind, "owner", kind, Protocol.ToElement(data));
    }
}
