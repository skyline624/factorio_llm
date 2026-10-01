using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class AttackResponseTests
{
    private static FactoryCell Cell(string id, string kind, params (string Role, string Id, double X, double Y)[] entities) =>
        new(id, 0, new(0, 0, true), kind, "stone-furnace", null, entities.ToDictionary(e => e.Role, e => e.Id), "ready", 1,
            Plan: entities.ToDictionary(e => e.Role, e => new PlannedEntity(e.Role, "stone-furnace", new(e.X, e.Y), 0)));

    [Fact]
    public void DamagedOwnEntitiesAreReadFromTheNativePhotograph()
    {
        var snapshot = IndustryClusterTests.Snapshot(IndustryClusterTests.Entity("10", "furnace", 5, 5, health: 120, maxHealth: 200),
            IndustryClusterTests.Entity("11", "furnace", 8, 5, health: 200, maxHealth: 200), IndustryClusterTests.Entity("12", "container", 9, 5));
        var damaged = Assert.Single(AttackMonitor.Damaged(snapshot));
        Assert.Equal(new DamagedEntity("10", new(5, 5), 120, 200), damaged);
    }

    [Fact]
    public async Task TheMonitorRecordsADestroyedRegisteredEntityOnceAndKeepsItInTheLog()
    {
        string directory = Directory.CreateTempSubdirectory("attack-log-").FullName;
        try
        {
            var state = new FactoryState(1, "world", [], [Cell("smelter-1", "smelter", ("furnace", "10", 5, 5), ("chest", "20", 7, 5))]);
            var snapshot = IndustryClusterTests.Snapshot(IndustryClusterTests.Entity("10", "furnace", 5, 5));
            var monitor = new AttackMonitor(directory, new Journal(), new ReflexEventLog());
            var first = Assert.Single(await monitor.ObserveAsync(state, snapshot, [new("biter", new(15, 5))], CancellationToken.None));
            Assert.Equal(("cluster-10", 1, 1, "E"), (first.Cluster, first.Destroyed, first.Enemies, first.Direction));
            Assert.Empty(await monitor.ObserveAsync(state, snapshot, [new("biter", new(16, 5))], CancellationToken.None));
            var log = await new AttackLog(directory).LoadAsync("world", CancellationToken.None);
            var kept = Assert.Single(log.Records);
            Assert.Equal((first.Tick, first.Cluster, first.Destroyed, first.Direction), (kept.Tick, kept.Cluster, kept.Destroyed, kept.Direction));
            Assert.Equal(first.Points, kept.Points);
            Assert.Equal(new[] { "20" }, log.Memory.Destroyed);
            await Assert.ThrowsAsync<InvalidDataException>(() => new AttackLog(directory).LoadAsync("other-world", CancellationToken.None));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ResponsesGoToTheClusterThatHoldsTheAttackEvenAfterItWasRenamed()
    {
        var bands = new IndustryCluster("cluster-120", new(new(10, 20), new(40, 45)), ["120"]);
        var rows = new IndustryCluster("cluster-7", new(new(-60, -10), new(-40, 10)), ["7"]);
        var record = new AttackRecord(500, "cluster-7", 1, 0, 0, 0, null, [new(-45, 5)], ["8"]);
        Assert.Equal("cluster-7", AttackResponseController.Locate([bands, rows], record)!.Id);
        // Growth merged the old cluster under another name: its attacked points still locate it.
        var renamed = rows with { Id = "cluster-3" };
        Assert.Equal("cluster-3", AttackResponseController.Locate([bands, renamed], record)!.Id);
        Assert.Null(AttackResponseController.Locate([bands], record));
    }

    [Theory]
    [InlineData(400, 0, 4)]   // Plenty of plates: the bounded four nests.
    [InlineData(100, 0, 1)]   // One turret (40 plates as gears and plates) and ten magazines (40 plates) per nest, plus copper.
    [InlineData(0, 2, 0)]     // Two carried turrets but no ammunition: a turret without rounds is no defense.
    [InlineData(80, 2, 2)]    // Carried turrets need only their magazines.
    public void NestsAreBoundedByWhatTheStockCanArm(long plates, long turrets, int expected)
    {
        var catalog = Catalogs.Early() with
        {
            Recipes = [.. Catalogs.Early().Recipes,
                new("gun-turret", true, "crafting", 8, [new("iron-gear-wheel", "item", 10), new("copper-plate", "item", 10), new("iron-plate", "item", 20)],
                    [new("gun-turret", "item", 1)], false),
                new("firearm-magazine", true, "crafting", 1, [new("iron-plate", "item", 4)], [new("firearm-magazine", "item", 1)], false)]
        };
        var stock = new Dictionary<string, long> { ["iron-plate"] = plates, ["copper-plate"] = 1000, ["gun-turret"] = turrets };
        Assert.Equal(expected, AttackResponseController.Affordable(catalog, "gun-turret", "firearm-magazine", 10, stock, 4));
    }

    [Fact]
    public void PlannerFactsSummarizeAttacksCoverageAndEquipmentWithoutCoordinates()
    {
        var state = new FactoryState(1, "world", [], [
            Cell("smelter-1", "smelter", ("furnace", "10", 5, 5)),
            new("perimeter-turret@5,-4", 0, new(0, 0, true), "turret", "gun-turret", null, new Dictionary<string, string> { ["turret"] = "50" }, "ready", 1,
                Plan: new Dictionary<string, PlannedEntity> { ["turret"] = new("turret", "gun-turret", new(5, -4), 0) })]);
        var snapshot = IndustryClusterTests.Snapshot(IndustryClusterTests.Entity("10", "furnace", 5, 5), IndustryClusterTests.Entity("11", "furnace", 80, 80));
        var defenses = new DefenseFactoryState(1, [new("10", new(5, 5)), new("11", new(80, 80))],
            [new InstalledTurret("50", "gun-turret", new(5, -4), "inventory:50:1", 100, true, 18, "firearm-magazine")]);
        var records = Enumerable.Range(1, 7).Select(i => new AttackRecord(100L * i, "cluster-10", 0, i, 2, 0, "E", [new(5 + i, 5)], ["10"],
            Responded: i < 7, NestsAdded: i == 6 ? 4 : 0, Outcome: i < 7 ? "turrets-deployed" : null)).ToArray();
        var agent = Protocol.ToElement(new
        {
            weapon = new { name = "submachine-gun", ammunition = "firearm-magazine", rounds = 100, ready = true },
            loadout = new { armor = "heavy-armor", carried = new[] { new { name = "firearm-magazine", kind = "ammo", count = 20 } } }
        });
        string json = System.Text.Json.JsonSerializer.Serialize(AttackResponseFacts.Build(records, state, snapshot, defenses, agent), Protocol.Json);
        var facts = System.Text.Json.JsonDocument.Parse(json).RootElement;
        var attacks = facts.GetProperty("recentAttacks").EnumerateArray().ToArray();
        Assert.Equal(new long[] { 700, 600, 500, 400, 300 }, attacks.Select(a => a.GetProperty("tick").GetInt64()));
        Assert.Equal(4, attacks[1].GetProperty("nestsAdded").GetInt32());
        var clusters = facts.GetProperty("clusters").EnumerateArray().ToArray();
        Assert.Equal(2, clusters.Length);
        var smelting = clusters.Single(c => c.GetProperty("cluster").GetString() == "cluster-10");
        Assert.Equal((1, 1, 1), (smelting.GetProperty("industry").GetInt32(), smelting.GetProperty("covered").GetInt32(), smelting.GetProperty("nests").GetInt32()));
        Assert.Equal(0, clusters.Single(c => c.GetProperty("cluster").GetString() == "cluster-11").GetProperty("covered").GetInt32());
        Assert.Equal(("heavy-armor", "submachine-gun", 20), (facts.GetProperty("actor").GetProperty("armor").GetString(),
            facts.GetProperty("actor").GetProperty("weapon").GetString(), facts.GetProperty("actor").GetProperty("carriedMagazines").GetInt32()));
        // Attacked points and entity ids stay in the private log: no coordinate reaches the model.
        Assert.DoesNotContain("points", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"x\"", json, StringComparison.Ordinal);
    }

    private sealed class Journal : IControllerJournal
    {
        public Task AppendAsync(string type, object data, CancellationToken token) => Task.CompletedTask;
    }
}
