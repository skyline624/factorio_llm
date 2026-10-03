using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FluidStageDeferralTests
{
    [Theory]
    [InlineData("supplied", true)]
    [InlineData("no-extractor", false)]
    [InlineData("no-refinery", false)]
    [InlineData("unfinished", false)]
    [InlineData("unfed-extractor", false)]
    [InlineData("unfed-refinery", false)]
    [InlineData("different-recipe", false)]
    [InlineData("empty-crude", false)]
    [InlineData("empty-gas", false)]
    [InlineData("unobserved-rate", false)]
    [InlineData("different-machine", false)]
    public async Task ExhaustedExpansionNeedsFreshNativeSuppliersAndRetainsCompleteTargets(string condition, bool expected)
    {
        string directory = Path.Combine(Path.GetTempPath(), "fluid-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var catalog = OilCatalogs.Oil();
            var plan = OilCatalogs.Plan(catalog, "plastic-bar", 120)!;
            var stage = plan.Stages.First();
            var source = plan.Sources.Single(s => s.Resource is not null);
            var registry = new FactoryRegistry(directory);
            var extractor = new FactoryCell("extractor", 0, new(0, 0, true), FluidCellBuilder.ExtractorKind, "pumpjack", "crude-oil",
                new Dictionary<string, string> { ["drill"] = "jack" }, "ready", 80);
            var refinery = new FactoryCell("refinery", 0, new(0, 0, true), FluidCellBuilder.MachineKind, "oil-refinery", "basic-oil-processing",
                new Dictionary<string, string> { ["machine"] = "refinery" }, condition == "unfinished" ? "building" : "ready", 80);
            var retained = new FactoryState(1, catalog.Scope.WorldId, [], [extractor, refinery]).WithTarget("plastic-bar", 120);
            await registry.SaveAsync(retained, default);
            var game = new SuppliedGame(catalog.Scope, condition);
            var journal = new Journal();
            var failure = new FluidExtractorSearchExhaustedException("crude-oil", catalog.Scope, 90, 64);
            var rates = new Dictionary<string, double> { [extractor.Id] = condition == "unobserved-rate" ? 0 : 199 };
            bool deferred = await new FluidChainDirector(game, journal, directory)
                .TryDeferExtractorSearchAsync(stage, catalog, source, rates, failure, default);
            Assert.Equal(expected, deferred);
            Assert.True(game.Photos > 1); // One atomic, complete photograph across several native pages.
            var after = await registry.LoadAsync(catalog.Scope.WorldId, default);
            Assert.Equal(120, after.Targets!["plastic-bar"]);
            Assert.Equal(retained.Cells.Select(c => (c.Id, c.Status, nativeId: c.Entities.Values.Single())),
                after.Cells.Select(c => (c.Id, c.Status, nativeId: c.Entities.Values.Single())));
            if (expected)
            {
                var data = Assert.Single(journal.Rows);
                Assert.Equal("extractor-search-exhausted", data.GetProperty("reason").GetString());
                Assert.Equal(stage.CraftsPerMinute, data.GetProperty("stage").GetProperty("craftsPerMinute").GetDouble());
                Assert.Equal(source.UnitsPerMinute, data.GetProperty("source").GetProperty("unitsPerMinute").GetDouble());
                Assert.True(data.GetProperty("targetRetained").GetBoolean());
                Assert.Equal(64, data.GetProperty("observations").GetInt32());
                Assert.Equal(100, data.GetProperty("collectedTick").GetInt64());
            }
            else Assert.Empty(journal.Rows);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("world")]
    [InlineData("incarnation")]
    [InlineData("generation")]
    [InlineData("search-scope")]
    [InlineData("resource")]
    [InlineData("older-photo")]
    public async Task IdentityAndObservationChangesCannotAuthorizeDeferral(string condition)
    {
        var catalog = OilCatalogs.Oil();
        var scope = condition switch
        {
            "world" => catalog.Scope with { WorldId = "other" },
            "incarnation" => catalog.Scope with { Incarnation = catalog.Scope.Incarnation + 1 },
            "generation" => catalog.Scope with { Generation = catalog.Scope.Generation + 1 },
            _ => catalog.Scope
        };
        var plan = OilCatalogs.Plan(catalog, "plastic-bar", 120)!;
        var failure = new FluidExtractorSearchExhaustedException(condition == "resource" ? "iron-ore" : "crude-oil",
            condition == "search-scope" ? catalog.Scope with { SessionId = "other" } : catalog.Scope,
            condition == "older-photo" ? 101 : 90, 64);
        var journal = new Journal();
        await Assert.ThrowsAsync<InvalidDataException>(() => new FluidChainDirector(new SuppliedGame(scope, "supplied"), journal, "unused")
            .TryDeferExtractorSearchAsync(plan.Stages.First(), catalog, plan.Sources.Single(s => s.Resource is not null),
                new Dictionary<string, double> { ["extractor"] = 199 }, failure, default));
        Assert.Empty(journal.Rows);
    }

    [Fact]
    public async Task UncertainNativeObservationPropagatesWithoutClaimingUsableSupply()
    {
        var catalog = OilCatalogs.Oil();
        var plan = OilCatalogs.Plan(catalog, "plastic-bar", 120)!;
        var error = new OperationOutcomeUnknownException("original-operation", new IOException("lost receipt"));
        var game = new SuppliedGame(catalog.Scope, "supplied") { Failure = error };
        var journal = new Journal();
        var observed = await Assert.ThrowsAsync<OperationOutcomeUnknownException>(() => new FluidChainDirector(game, journal, "unused")
            .TryDeferExtractorSearchAsync(plan.Stages.First(), catalog, plan.Sources.Single(s => s.Resource is not null),
                new Dictionary<string, double> { ["extractor"] = 199 }, new("crude-oil", catalog.Scope, 90, 64), default));
        Assert.Same(error, observed);
        Assert.Equal("original-operation", observed.OperationId);
        Assert.Empty(journal.Rows);
    }

    private sealed class Journal : IControllerJournal
    {
        public List<System.Text.Json.JsonElement> Rows { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            Assert.Equal("fluid-chain-stage-short", type);
            Rows.Add(Protocol.ToElement(data));
            return Task.CompletedTask;
        }
    }

    private sealed class SuppliedGame(ActorScope scope, string condition) : IGameClient
    {
        public int Photos { get; private set; }
        public Exception? Failure { get; init; }
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal("factory_snapshot", request.Action); // No new native mutation is permitted on the refusal path.
            if (Failure is not null) throw Failure;
            Photos++;
            FactoryRecord[] records = [
                Entity("source", "steam-engine", "generator", 1),
                Entity("jack", "pumpjack", "mining-drill", condition == "unfed-extractor" ? 2 : 1),
                Entity("refinery", condition == "different-machine" ? "chemical-plant" : "oil-refinery", "assembling-machine", condition == "unfed-refinery" ? 2 : 1),
                new("work", "work", "refinery", "machine-craft", Protocol.ToElement(new { recipe = condition == "different-recipe" ? "advanced-oil-processing" : "basic-oil-processing" })),
                Fluid("crude", "jack", "crude-oil", condition == "empty-crude" ? 0 : 200),
                Fluid("gas", "refinery", "petroleum-gas", condition == "empty-gas" ? 0 : 90)
            ];
            records = records.Where(r => !(condition == "no-extractor" && r.EntityId == "jack")
                && !(condition == "no-refinery" && r.EntityId == "refinery")).ToArray();
            int offset = request.Arguments.TryGetProperty("offset", out var value) ? value.GetInt32() : 0;
            var page = records.Skip(offset).Take(2).ToArray();
            var data = new FactorySnapshotPage("fresh", scope, scope, 100, 1000, records.Length, offset, offset + page.Length,
                offset + page.Length == records.Length, Protocol.ToElement(new { atomic = true, knownInventoriesComplete = true,
                    knownBeltAndInserterTransitComplete = true, fluidSegmentsDeduplicated = true }), page);
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 100 + Photos, Protocol.ToElement(data)));
        }
        private static FactoryRecord Entity(string id, string name, string type, int network) =>
            new(id, "entity", id, name, Protocol.ToElement(new { role = "factory", type, electricNetworkId = network }));
        private static FactoryRecord Fluid(string id, string entity, string name, double amount) =>
            new(id, "fluid", entity, name, Protocol.ToElement(new { aggregateSafe = true, contents = new Dictionary<string, double> { [name] = amount } }));
    }
}
