using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ConstructionSupplyTests
{
    private static readonly ProductionCatalog Catalog = ConstructionSupplyPlannerTests.Catalog();
    private static readonly Dictionary<string, int> Kit = new() { ["burner-mining-drill"] = 1, ["stone-furnace"] = 1 };
    private static readonly Dictionary<string, long> Carried = Kit.ToDictionary(p => p.Key, p => (long)p.Value);

    [Fact]
    public async Task AlreadyCarriedKitIsProvedWithoutAnyMutationOrDuplicateCraft()
    {
        var game = new Observations();
        var journal = new Journal();
        await CarriedStock.EnsureAsync(game, journal, Catalog, Kit, default);
        Assert.Equal(["construction-supply-plan", "construction-supply-result"], journal.Rows.Select(p => p.Type));
        Assert.All(game.Actions, action => Assert.Equal("observe", action));
        Assert.Equal(1, journal.Rows[1].Data.GetProperty("carried").GetProperty("stone-furnace").GetInt64());
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("scope")]
    [InlineData("incomplete")]
    public async Task NativeChangesDuringProcurementBlockConstructionProof(string change)
    {
        var game = new Observations(change, change == "incomplete" ? 4 : 2);
        var journal = new Journal();
        if (change == "manual")
            await Assert.ThrowsAsync<InvalidOperationException>(() => CarriedStock.EnsureAsync(game, journal, Catalog, Kit, default));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => CarriedStock.EnsureAsync(game, journal, Catalog, Kit, default));
        Assert.DoesNotContain(journal.Rows, p => p.Type == "construction-supply-result");
        Assert.All(game.Actions, action => Assert.Equal("observe", action));
    }

    [Fact]
    public async Task StaleOrIncompleteInventoriesCannotBeUsedToPlanTheKit()
    {
        var game = new Observations("coverage", 1);
        var journal = new Journal();
        await Assert.ThrowsAsync<InvalidDataException>(() => CarriedStock.EnsureAsync(game, journal, Catalog, Kit, default));
        Assert.Empty(journal.Rows);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, true, false)]
    public async Task OnlyUnprotectedCollectableOutputsShortCircuitIngredientExpansion(bool reserved, bool collectable,
        bool turret, bool stored)
    {
        var catalog = Catalog with { Recipes = [new("firearm-magazine", true, "crafting", 1,
            [new("iron-plate", "item", 4)], [new("firearm-magazine", "item", 1)], false)] };
        var game = new Observations("scope", 2, [new
        {
            id = "source", name = turret ? "gun-turret" : "iron-chest", type = turret ? "ammo-turret" : "container",
            position = new MapPosition(0, 0), inventories = new { output = new { items = new Dictionary<string, long> { ["firearm-magazine"] = 10 } } }
        }]);
        var journal = new Journal();
        using (ProductionReservations.Enter(reserved ? new HashSet<string> { "source" } : [],
            collectable ? new HashSet<string> { "source" } : []))
            await Assert.ThrowsAsync<InvalidDataException>(() => CarriedStock.EnsureAsync(game, journal, catalog,
                new Dictionary<string, int> { ["firearm-magazine"] = 1 }, default));
        var material = Assert.Single(journal.Rows[0].Data.GetProperty("plan").GetProperty("materials").EnumerateArray());
        Assert.Equal(stored ? "firearm-magazine" : "iron-plate", material.GetProperty("item").GetString());
        Assert.All(game.Actions, action => Assert.Equal("observe", action));
    }

    private sealed class Observations(string? change = null, int changeAt = int.MaxValue, object[]? observedEntities = null) : IGameClient
    {
        public List<string> Actions { get; } = [];
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Actions.Add(request.Action);
            Assert.Equal("observe", request.Action);
            bool changed = Actions.Count >= changeAt;
            long tick = Actions.Count;
            return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(new
            {
                scope = changed && change == "scope" ? Catalog.Scope with { Incarnation = Catalog.Scope.Incarnation + 1 } : Catalog.Scope,
                collectedTick = tick,
                coverage = new { knownInventoriesComplete = !(changed && change == "coverage") },
                agent = new
                {
                    alive = true, controlMode = changed && change == "manual" ? "manual" : "ai",
                    inventory = changed && change == "incomplete" ? new Dictionary<string, long>() : Carried,
                    position = new MapPosition(0, 0)
                },
                entities = observedEntities ?? []
            })));
        }
    }

    private sealed class Journal : IControllerJournal
    {
        public List<(string Type, JsonElement Data)> Rows { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            Rows.Add((type, Protocol.ToElement(data)));
            return Task.CompletedTask;
        }
    }
}
