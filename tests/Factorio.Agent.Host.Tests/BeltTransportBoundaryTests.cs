using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class BeltTransportBoundaryTests
{
    [Fact]
    public void RefilledBufferIncludesRootProductionAndAllIntermediateStocks()
    {
        var map = Map();
        var before = Snapshot(rootStock: 2, cycles: 10, bufferStock: 8, upstreamTransit: 1, downstreamTransit: 0, targetStock: 0);
        var boundary = BeltTransportBoundary.From(map, before, Catalog(map), "source", "target", "gear");
        var target = MaterialEndpoint.From(before, Catalog(map), "target", "gear", false);
        var baseline = boundary.Read(before, target, "gear", ["out"], ["extract", "receive"]);
        // Five new products plus one unit from root stock yield six deliveries; intermediate totals remain stable.
        var after = Snapshot(rootStock: 1, cycles: 15, bufferStock: 5, upstreamTransit: 2, downstreamTransit: 2, targetStock: 6);
        Assert.Equal("root", boundary.Root.EntityId);
        Assert.Contains("source", boundary.ReservedEntityIds);
        Assert.Contains("root", boundary.ReservedEntityIds);
        Assert.Equal(6, boundary.Read(after, target, "gear", ["out"], ["extract", "receive"]).DeliveredSince(baseline));
    }

    [Theory]
    [InlineData("second-feeder")]
    [InlineData("cycle")]
    [InlineData("missing-lane")]
    [InlineData("lost-buffer-stock")]
    public void AmbiguousOrIncompleteUpstreamFlowCannotProveDelivery(string mutation)
    {
        var map = Map();
        var before = Snapshot(2, 10, 8, 1, 0, 0);
        if (mutation == "second-feeder") map = map with { Entities = [.. map.Entities, Arm("extra", "unknown", "source")] };
        if (mutation == "cycle") map = map with { Entities = map.Entities.Select(e => e.Id == "feed" ? e with { PickupTargetId = "source" } : e).ToArray() };
        if (mutation is "second-feeder" or "cycle")
        {
            Assert.Throws<InvalidDataException>(() => BeltTransportBoundary.From(map, before, Catalog(map), "source", "target", "gear"));
            return;
        }
        var boundary = BeltTransportBoundary.From(map, before, Catalog(map), "source", "target", "gear");
        var target = MaterialEndpoint.From(before, Catalog(map), "target", "gear", false);
        var baseline = boundary.Read(before, target, "gear", ["out"], ["extract", "receive"]);
        var after = mutation == "missing-lane"
            ? before with { Records = before.Records.Where(r => r.Id != "up:2").ToArray() }
            : Snapshot(2, 10, 7, 1, 0, 0);
        Assert.Throws<InvalidDataException>(() => boundary.Read(after, target, "gear", ["out"], ["extract", "receive"]).DeliveredSince(baseline));
    }

    [Fact]
    public void ANewFeederAfterTheBaselineInvalidatesTheBoundary()
    {
        var map = Map();
        var boundary = BeltTransportBoundary.From(map, Snapshot(2, 10, 8, 1, 0, 0), Catalog(map), "source", "target", "gear");
        map = map with { Entities = [.. map.Entities, Arm("extra", "unknown", "source")] };
        Assert.Throws<InvalidDataException>(() => boundary.Verify(map));
    }

    [Fact]
    public void AConfiguredButLockedMachineCannotStartAProductionBoundary()
    {
        var map = Map();
        var catalog = Catalog(map);
        catalog = catalog with { Recipes = catalog.Recipes.Select(r => r with { Enabled = false }).ToArray() };
        Assert.Throws<InvalidOperationException>(() => BeltTransportBoundary.From(map, Snapshot(2, 10, 8, 1, 0, 0), catalog, "source", "target", "gear"));
    }

    internal static SpatialSnapshot Map()
    {
        var map = BeltTransportPlannerTests.Map(true);
        return map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["machine"] = map.Prototypes["chest"] with { Name = "machine", Type = "assembling-machine" } },
            Entities = [.. map.Entities,
                new("root", "machine", new(-5.5, .5), new(new(-6.9, -.9), new(-4.1, 1.9)), 0, "own"),
                Arm("feed", "root", "up"), Arm("buffer-arm", "up", "source"),
                Belt("up"), Belt("out"), Arm("extract", "source", "out"), Arm("receive", "out", "target")]
        };
    }

    private static SpatialEntity Arm(string id, string source, string target) =>
        new(id, "arm", new(0, 0), new(new(0, 0), new(1, 1)), 0, "own", Power: new(10, 1), PickupTargetId: source, DropTargetId: target);
    private static SpatialEntity Belt(string id) =>
        new(id, "belt", new(0, 0), new(new(0, 0), new(1, 1)), 4, "own", BeltConnections: new([], []));
    internal static ProductionCatalog Catalog(SpatialSnapshot map) => new(map.Scope, 0,
        [new("gear", true, "crafting", .5, [new("iron", "item", 2)], [new("gear", "item", 1)], false)],
        new Dictionary<string, NativeItem>(), new Dictionary<string, NativeMaterial[]>(),
        new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());

    internal static FactorySnapshot Snapshot(long rootStock, long cycles, long bufferStock, long upstreamTransit, long downstreamTransit, long targetStock)
    {
        var records = new List<FactoryRecord>();
        Add("root", "entity", "root", new { type = "assembling-machine" });
        Add("root:work", "work", "root", new { recipe = "gear", inputInventoryId = "root:input", outputInventoryId = "root:inventory", productsFinished = cycles, inProcess = false });
        Add("root:input", "inventory", "root", new { items = new { iron = 0 } });
        foreach (var (id, stock) in new[] { ("root", rootStock), ("source", bufferStock), ("target", targetStock) })
        {
            if (id != "root") Add(id, "entity", id, new { type = "container" });
            Add(id + ":inventory", "inventory", id, new { items = new { gear = stock } });
        }
        foreach (var (id, stock) in new[] { ("up", upstreamTransit), ("out", downstreamTransit) })
        {
            Add(id + ":1", "transit", id, new { items = new { gear = stock } });
            Add(id + ":2", "transit", id, new { items = new { gear = 0 } });
        }
        foreach (string id in new[] { "feed", "buffer-arm", "extract", "receive" }) Add(id + ":hand", "transit", id, new { items = new { gear = 0 } });
        return new("test", Map().Scope, 0, 1000, JsonSerializer.SerializeToElement(new { atomic = true }), records);

        void Add(string id, string kind, string entity, object data) => records.Add(new(id, kind, entity, entity, JsonSerializer.SerializeToElement(data)));
    }
}
