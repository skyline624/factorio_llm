using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryTransportConversionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(13)]
    public void LegacyTwoConsumerConversionRetainsNativeEndpointsAndPersistsExactRetirements(int? reserve)
    {
        var fixture = BalancedBeltPlannerTests.OriginalLine(extend: true);
        var map = fixture.Map with { Items = new Dictionary<string, PlaceableItem>(fixture.Map.Items)
            { ["transport-belt"] = fixture.Map.Items["belt"], ["inserter"] = fixture.Map.Items["arm"], ["small-electric-pole"] = fixture.Map.Items["pole"] } };
        int prefixCount = fixture.Plan.Belts.Count;
        string[] prefix = fixture.Belts.Take(prefixCount).ToArray();
        var obsolete = fixture.Belts.Skip(prefixCount).Append("old-second-arm").ToHashSet(StringComparer.Ordinal);
        var plan = new BalancedBeltPlanner().FindExisting(map, new("transport-belt", "inserter", "small-electric-pole"), "splitter",
            "source", "target", "second", "old-source-arm", "old-first-arm", prefix, fixture.Poles, obsolete);
        Assert.NotNull(plan);
        var entities = new Dictionary<string, string> { ["source-inserter"] = "old-source-arm",
            ["target-inserter-0"] = "old-first-arm", ["target-inserter-1"] = "old-second-arm" };
        for (int i = 0; i < fixture.Belts.Length; i++) entities[$"belt-{i}"] = fixture.Belts[i];
        for (int i = 0; i < fixture.Poles.Length; i++) entities[$"pole-{i}"] = fixture.Poles[i];
        var parts = entities.ToDictionary(p => p.Key, p =>
        {
            var native = map.Entities.Single(e => e.Id == p.Value);
            string item = native.Name switch { "belt" => "transport-belt", "arm" => "inserter", "pole" => "small-electric-pole", _ => throw new InvalidDataException() };
            return new PlannedEntity(p.Key, item, native.Position, native.Direction);
        });
        var old = new FactoryCell("existing-line", 0, new(0, 0, true), "transport", "transport-belt", null, entities, "ready", 1, Plan: parts);
        var bus = new FactoryTransportBus("existing-bus", "producer-cell", "iron-gear-wheel", old.Id,
            [new("first-cell", "target-inserter-0", 40), new("second-cell", "target-inserter-1", 80)], ActorReserve: reserve);
        var record = FactoryTransportConversion.CreateRecord(bus, old, plan, map);
        Assert.Equal(old.Id, record.Cell.Id);
        Assert.Equal(bus.Id, record.Bus.Id);
        Assert.Equal("building", record.Cell.Status);
        Assert.Equal(reserve ?? 0, record.Bus.ActorReserve);
        Assert.Equal("old-source-arm", record.Cell.Entities["source-inserter"]);
        Assert.Equal("old-first-arm", record.Cell.Entities["target-inserter-0"]);
        Assert.All(fixture.Poles, id => Assert.Contains(id, record.Cell.Entities.Values));
        var expected = obsolete.Append(prefix[plan.ReplacedBelt]).ToHashSet(StringComparer.Ordinal);
        var retired = record.Bus.PendingRetirements!.Select(r => r.EntityId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(prefix[plan.ReplacedBelt], retired);
        Assert.True(retired.IsSubsetOf(expected));
        Assert.All(expected.Except(retired), id => Assert.Contains(id, record.Cell.Entities.Values));
        Assert.All(prefix.Where(id => id != prefix[plan.ReplacedBelt]), id => Assert.Contains(id, record.Cell.Entities.Values));
        Assert.DoesNotContain(record.Cell.Entities.Values, id => retired.Contains(id));
        Assert.True(old.Entities.Values.ToHashSet(StringComparer.Ordinal).SetEquals(record.Cell.Entities.Values.Concat(retired)));
        Assert.Equal(2, record.Bus.Graph!["splitter-0"].Outputs.Count);
        Assert.Equal(new[] { 40, 80 }, record.Bus.Consumers.Select(c => c.Maximum));
        var roundtrip = JsonSerializer.Deserialize<FactoryTransportBus>(JsonSerializer.Serialize(record.Bus, Protocol.Json), Protocol.Json)!;
        Assert.Equal(record.Bus.PendingRetirements!.Select(r => r.EntityId), roundtrip.PendingRetirements!.Select(r => r.EntityId));
        Assert.All(roundtrip.PendingRetirements!, r =>
        {
            var native = map.Entities.Single(e => e.Id == r.EntityId);
            Assert.Equal(native.Position, r.Part.Position);
            Assert.Equal(native.Direction, r.Part.Direction);
        });
    }

    [Fact]
    public void SourceRecoveryAdoptsOnlyTheReplacementArmWithoutAdoptingObsoleteFutureParts()
    {
        var scope = new ActorScope("world", "session", "actor", 1, 1);
        var catalog = new ProductionCatalog(scope, 1, [], new Dictionary<string, NativeItem>
            { ["inserter"] = new(0, 50, PlaceEntity: "inserter") }, new Dictionary<string, NativeMaterial[]>(),
            new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
        var plan = new Dictionary<string, PlannedEntity>
        {
            ["source-inserter"] = new("source-inserter", "inserter", new(1, 1), 0),
            ["belt-77"] = new("belt-77", "transport-belt", new(3, 3), 4)
        };
        var cell = new FactoryCell("line", 0, new(0, 0, true), "transport", "transport-belt", null,
            new Dictionary<string, string> { ["source-inserter"] = "lost-source", ["belt-77"] = "retained-belt" }, "building", 1, Plan: plan);
        var snapshot = new FactorySnapshot("native", scope, 2, 100, Protocol.ToElement(new { }),
        [Entity("replacement", "inserter", new(1, 1), 0), Entity("obsolete-tail", "transport-belt", new(3, 3), 4)]);

        var reconciled = FactoryTransportConversion.ReconcileSource(cell, snapshot, catalog, new HashSet<string>());

        Assert.Equal("replacement", reconciled.Entities["source-inserter"]);
        Assert.Equal("retained-belt", reconciled.Entities["belt-77"]);
        Assert.Same(plan, reconciled.Plan);
        Assert.Equal("building", reconciled.Status);
        reconciled = FactoryTransportConversion.ReconcileSource(cell, snapshot, catalog, new HashSet<string> { "replacement" });
        Assert.False(reconciled.Entities.ContainsKey("source-inserter"));
        Assert.Equal("retained-belt", reconciled.Entities["belt-77"]);
    }

    [Theory]
    [InlineData(0, "", false, true)]
    [InlineData(0, "", true, false)]
    [InlineData(1, "source-arm", false, true)]
    [InlineData(1, "source-arm", true, true)]
    [InlineData(1, "foreign", false, false)]
    [InlineData(2, "source-arm", false, false)]
    [InlineData(1, "", false, false)]
    [InlineData(0, "source-arm", false, false)]
    public void SourceCircuitMustAccountForEveryNativeWireBeforeConversionOrRecovery(int count, string neighbour,
        bool requireConnection, bool allowed)
    {
        var snapshot = new FactorySnapshot("native", new("world", "session", "actor", 1, 1), 2, 100, Protocol.ToElement(new { }),
        [new("chest", "entity", "source-chest", "iron-chest", Protocol.ToElement(new
            { transport = new { redNeighbourCount = count, redNeighbours = neighbour.Length == 0 ? Array.Empty<string>() : new[] { neighbour } } }))]);
        Assert.Equal(allowed, FactoryTransportConversion.SourceWiringIsExclusive(snapshot, "source-chest", "source-arm", requireConnection));
    }

    [Fact]
    public void EmptyLuaCircuitTableIsUnwiredButUnknownCircuitCountsNeverAllowRecovery()
    {
        var snapshot = new FactorySnapshot("native", new("world", "session", "actor", 1, 1), 2, 100, Protocol.ToElement(new { }),
        [new("chest", "entity", "source-chest", "iron-chest", Protocol.ToElement(new
            { transport = new { redNeighbourCount = 0, redNeighbours = new { } } }))]);
        Assert.True(FactoryTransportConversion.SourceWiringIsExclusive(snapshot, "source-chest", null, false));
        Assert.False(FactoryTransportConversion.SourceWiringIsExclusive(snapshot, "source-chest", null, true));
        snapshot = snapshot with { Records = [new("chest", "entity", "source-chest", "iron-chest",
            Protocol.ToElement(new { transport = new { redNeighbours = Array.Empty<string>() } }))] };
        Assert.False(FactoryTransportConversion.SourceWiringIsExclusive(snapshot, "source-chest", null, false));
    }

    private static FactoryRecord Entity(string id, string name, MapPosition position, int direction) => new(id, "entity", id, name,
        Protocol.ToElement(new { role = "factory", position, direction, type = name, force = "own", surfaceIndex = 1 }));

    [Fact]
    public void JointCapacityDoesNotCountOneEmptySlotForTwoDifferentItems()
    {
        var (snapshot, catalog) = Bag(1, Array.Empty<object>(), Array.Empty<object>());
        Assert.False(FactoryTransportRecoveryCapacity.Fits(snapshot, catalog, new Dictionary<string, long> { ["transport-belt"] = 1, ["iron-gear-wheel"] = 1 }));
        (snapshot, catalog) = Bag(2, Array.Empty<object>(), Array.Empty<object>());
        Assert.True(FactoryTransportRecoveryCapacity.Fits(snapshot, catalog, new Dictionary<string, long> { ["transport-belt"] = 1, ["iron-gear-wheel"] = 1 }));
    }

    [Fact]
    public void ProvenNormalPartialStacksKeepRecoveryResumableAfterTheFirstPiece()
    {
        var (snapshot, catalog) = Bag(2, new object[] { new { slot = 1, name = "transport-belt", quality = "normal", count = 1 } }, Array.Empty<object>());
        Assert.True(FactoryTransportRecoveryCapacity.Fits(snapshot, catalog, new Dictionary<string, long> { ["transport-belt"] = 99, ["iron-gear-wheel"] = 50 }));
        Assert.False(FactoryTransportRecoveryCapacity.Fits(snapshot, catalog, new Dictionary<string, long> { ["transport-belt"] = 100, ["iron-gear-wheel"] = 50 }));
    }

    [Fact]
    public void FilteredEmptySlotsAndDamagedToolsCannotBePromisedToAnyItem()
    {
        var (snapshot, catalog) = Bag(1, Array.Empty<object>(), new object[] { new { slot = 1, filter = new { name = "transport-belt" } } });
        Assert.False(FactoryTransportRecoveryCapacity.Fits(snapshot, catalog, new Dictionary<string, long> { ["transport-belt"] = 1 }));
        (snapshot, catalog) = Bag(1, new object[] { new { slot = 1, name = "transport-belt", quality = "normal", count = 1, durability = 1 } }, Array.Empty<object>());
        Assert.False(FactoryTransportRecoveryCapacity.Fits(snapshot, catalog, new Dictionary<string, long> { ["transport-belt"] = 1 }));
    }

    [Fact]
    public void EmptyLuaTablesAreCompleteButUnknownOrMismatchedCapacityIsRefused()
    {
        var (snapshot, catalog) = Bag(2, new { }, new { });
        Assert.True(FactoryTransportRecoveryCapacity.Fits(snapshot, catalog, new Dictionary<string, long> { ["transport-belt"] = 1 }));
        Assert.Throws<InvalidDataException>(() => FactoryTransportRecoveryCapacity.Fits(snapshot,
            catalog with { Scope = catalog.Scope with { Generation = 2 } }, new Dictionary<string, long> { ["transport-belt"] = 1 }));
        (snapshot, catalog) = Bag(2, new { invalid = 1 }, Array.Empty<object>());
        Assert.Throws<InvalidDataException>(() => FactoryTransportRecoveryCapacity.Fits(snapshot, catalog, new Dictionary<string, long> { ["transport-belt"] = 1 }));
    }

    [Fact]
    public void RecoveryReservesAllMovingBusContentsAndRejectsForeignItemsAndQuality()
    {
        var (snapshot, _) = Bag(4, Array.Empty<object>(), Array.Empty<object>());
        snapshot = snapshot with { Records = [.. snapshot.Records,
            new("transit:first", "transit", "retained-belt", "transport-line", Protocol.ToElement(new { items = new Dictionary<string, long> { ["iron-gear-wheel"] = 20 } })),
            new("transit:second", "transit", "obsolete-belt", "transport-line", Protocol.ToElement(new { items = new Dictionary<string, long> { ["iron-gear-wheel"] = 5 } }))] };
        FactoryTransportRetirement[] pending = [new("obsolete-belt", new("belt-1", "transport-belt", new(1, 1), 4))];
        var all = new HashSet<string> { "retained-belt", "obsolete-belt" };
        var incoming = FactoryTransportRecoveryCapacity.Incoming(snapshot, pending, all, "iron-gear-wheel");
        Assert.Equal(1, incoming["transport-belt"]);
        Assert.Equal(26, incoming["iron-gear-wheel"]);
        snapshot = snapshot with { Records = [.. snapshot.Records,
            new("held", "transit", "obsolete-belt", "inserter-hand", Protocol.ToElement(new { items = new Dictionary<string, long> { ["iron-gear-wheel@uncommon"] = 1 } }))] };
        Assert.Throws<InvalidDataException>(() => FactoryTransportRecoveryCapacity.Incoming(snapshot, pending, all, "iron-gear-wheel"));
    }

    private static (FactorySnapshot Snapshot, ProductionCatalog Catalog) Bag(int slots, object stacks, object filters)
    {
        var scope = new ActorScope("world", "session", "actor", 1, 1);
        var items = new Dictionary<string, NativeItem> { ["transport-belt"] = new(0, 100), ["iron-gear-wheel"] = new(0, 100) };
        var catalog = new ProductionCatalog(scope, 1, [], items, new Dictionary<string, NativeMaterial[]>(),
            new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
        var hints = items.ToDictionary(p => p.Key, p => new { insertable = 1000, canInsertOne = true, certainty = "native-estimate" });
        var snapshot = new FactorySnapshot("snapshot", scope, 2, 100, Protocol.ToElement(new { }), [
            new("actor", "entity", "actor", "character", Protocol.ToElement(new { role = "actor", mainInventoryId = "main" })),
            new("main", "inventory", "actor", "main", Protocol.ToElement(new { slots, usableSlots = slots, stacks, filters, capacityHints = hints }))]);
        return (snapshot, catalog);
    }
}
