using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FluidExtractorSnapshotTests
{
    [Fact]
    public async Task KnownDistantExtractorsDoNotRequireTravelToMeasureTheirYield()
    {
        var catalog = OilCatalogs.Oil();
        var cells = new[] { Cell("first", "jack-1", new(410, 130)), Cell("second", "jack-2", new(900, -300)) };
        var snapshot = Snapshot(catalog, [.. Records("jack-1", 150000), .. Records("jack-2", 300000)]);
        var game = new SnapshotGame(catalog, snapshot);
        var journal = new Journal();
        string directory = Path.Combine(Path.GetTempPath(), $"factorio-yield-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            await new FactoryRegistry(directory).SaveAsync(new(1, catalog.Scope.WorldId, [], cells), default);
            var rates = await new FluidCellBuilder(game, journal, directory).ExtractorRatesAsync("crude-oil");
            Assert.Equal(300, rates["first"], 6);
            Assert.Equal(600, rates["second"], 6);
            Assert.All(game.Calls, action => Assert.Contains(action, new[] { "production_catalog", "factory_snapshot" }));
            Assert.Equal(2, journal.Result.GetProperty("snapshotCells").GetArrayLength());
            Assert.Empty(journal.Result.GetProperty("visited").EnumerateArray());
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(directory));
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("missing-work")]
    [InlineData("legacy-work")]
    [InlineData("wrong-resource")]
    [InlineData("wrong-machine")]
    [InlineData("unowned")]
    public void MissingOrUnrelatedNativeFactsCannotProveExtractorCapacity(string situation)
    {
        var catalog = OilCatalogs.Oil();
        var records = Records("jack", 150000).ToArray();
        if (situation == "missing-work") records = [records[0]];
        if (situation == "legacy-work") records[1] = records[1] with { Data = Protocol.ToElement(new { targetId = "oil", targetName = "crude-oil" }) };
        if (situation == "wrong-resource") records[1] = records[1] with { Data = Protocol.ToElement(new { targetId = "ore", targetName = "iron-ore", extraction = new { } }) };
        if (situation == "wrong-machine") records[0] = records[0] with { Name = "electric-mining-drill" };
        if (situation == "unowned") records[0] = records[0] with { Data = Protocol.ToElement(new { role = "actor", type = "mining-drill" }) };
        Assert.Null(FluidCellBuilder.Rate(Snapshot(catalog, records), catalog, Cell("cell", "jack", new(0, 0))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OldOrDifferentActorPhotographsCannotProveCurrentCapacity(bool differentActor)
    {
        var catalog = OilCatalogs.Oil();
        var snapshot = Snapshot(catalog, Records("jack", 150000).ToArray());
        snapshot = differentActor ? snapshot with { Scope = snapshot.Scope with { Incarnation = snapshot.Scope.Incarnation + 1 } }
            : snapshot with { CollectedTick = catalog.CollectedTick - 1 };
        Assert.Throws<InvalidDataException>(() => FluidCellBuilder.Rate(snapshot, catalog, Cell("cell", "jack", new(0, 0))));
    }

    [Theory]
    [InlineData(0, 300000)]
    [InlineData(-1, 300000)]
    [InlineData(150000, 0)]
    public void InvalidNativeYieldIsRejected(double amount, double normal)
    {
        var catalog = OilCatalogs.Oil();
        Assert.Throws<InvalidDataException>(() => FluidCellBuilder.Rate(
            Snapshot(catalog, Records("jack", amount, normal).ToArray()), catalog, Cell("cell", "jack", new(0, 0))));
    }

    private static FactoryCell Cell(string id, string drill, MapPosition position) => new(id, 0, new(0, 0, true),
        FluidCellBuilder.ExtractorKind, "pumpjack", "crude-oil", new Dictionary<string, string> { ["drill"] = drill }, "ready", 1,
        Plan: new Dictionary<string, PlannedEntity> { ["drill"] = new("drill", "pumpjack", position, 0) });

    private static IEnumerable<FactoryRecord> Records(string drill, double amount, double normal = 300000)
    {
        yield return new(drill, "entity", drill, "pumpjack", Protocol.ToElement(new { role = "factory", type = "mining-drill" }));
        yield return new($"work:{drill}", "work", drill, "native-mining", Protocol.ToElement(new
        {
            targetId = $"oil:{drill}", targetName = "crude-oil",
            extraction = new { amount, miningSpeed = 1d, miningTime = 1d, infiniteResource = true, normalResourceAmount = normal }
        }));
    }

    private static FactorySnapshot Snapshot(ProductionCatalog catalog, IReadOnlyList<FactoryRecord> records) => new("yield", catalog.Scope,
        catalog.CollectedTick + 1, catalog.CollectedTick + 1000, Protocol.ToElement(new
        { atomic = true, knownInventoriesComplete = true, knownBeltAndInserterTransitComplete = true, fluidSegmentsDeduplicated = true }), records);

    private sealed class SnapshotGame(ProductionCatalog catalog, FactorySnapshot snapshot) : IGameClient
    {
        public List<string> Calls { get; } = [];
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            object result = request.Action switch
            {
                "production_catalog" => catalog,
                "factory_snapshot" => new { snapshot.SnapshotId, scope = snapshot.Scope, snapshotScope = snapshot.Scope,
                    snapshot.CollectedTick, snapshot.ExpiresTick, totalRecords = snapshot.Records.Count, offset = 0,
                    nextOffset = snapshot.Records.Count, complete = true, snapshot.Coverage, snapshot.Records },
                _ => throw new InvalidOperationException($"Yield inspection must not call {request.Action}.")
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true,
                request.Action == "production_catalog" ? catalog.CollectedTick : snapshot.CollectedTick, Protocol.ToElement(result)));
        }
    }

    private sealed class Journal : IControllerJournal
    {
        public JsonElement Result { get; private set; }
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            if (type == "fluid-extractor-rates") Result = Protocol.ToElement(data);
            return Task.CompletedTask;
        }
    }
}
