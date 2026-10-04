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
