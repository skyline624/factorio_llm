using System.Text.Json;
using System.Text.Json.Nodes;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryTransportHealthTests
{
    [Fact]
    public void KnownFactoryGraphFeedsTwoConsumersAndSurvivesEndpointIdentityReplacement()
    {
        var (state, snapshot, bus) = Fixture();
        Assert.True(FactoryTransportHealth.Healthy(state, snapshot, bus));
        Assert.Equal(2, FactoryTransportHealth.Connected(state, snapshot).Count);
        var target = state.Cells.Single(c => c.Id == "first");
        state = state.With(target with { Entities = new Dictionary<string, string> { ["input-chest"] = "replacement" } });
        snapshot = snapshot with { Records = snapshot.Records.Select(r => r.EntityId == "first-chest" ? r with { Id = "replacement", EntityId = "replacement" }
            : r.EntityId == "receiver-0" ? Change(r, "transport.dropTargetId", "replacement", "transport.inserterControl.redNeighbours", new[] { "replacement" }) : r).ToArray() };
        Assert.True(FactoryTransportHealth.Healthy(state, snapshot, bus));
    }

    [Theory]
    [InlineData("filter")]
    [InlineData("quality")]
    [InlineData("script-disabled")]
    [InlineData("wire")]
    [InlineData("chest-wire")]
    [InlineData("foreign-belt")]
    [InlineData("missing-belt")]
    [InlineData("power")]
    [InlineData("force")]
    [InlineData("foreign-extractor")]
    public void IncompleteOrForeignNativeFlowsLeaveActorFallbackAvailable(string fault)
    {
        var (state, snapshot, bus) = Fixture();
        var rows = snapshot.Records.ToList();
        string id = fault is "foreign-belt" or "missing-belt" ? "b0" : fault == "chest-wire" ? "first-chest" : "receiver-0";
        int index = rows.FindIndex(r => r.EntityId == id);
        rows[index] = fault switch
        {
            "filter" => Change(rows[index], "transport.inserterControl.useFilters", false),
            "quality" => Change(rows[index], "transport.inserterControl.nativeNormalFilters", false),
            "script-disabled" => Change(rows[index], "transport.inserterControl.scriptDisabled", true),
            "wire" => Change(rows[index], "transport.inserterControl.redNeighbourCount", 2),
            "chest-wire" => Change(rows[index], "transport.redNeighbours", new[] { "receiver-0", "foreign" }, "transport.redNeighbourCount", 2),
            "foreign-belt" => Change(rows[index], "transport.beltConnections.inputsCount", 1),
            "power" => Change(rows[index], "electricNetworkId", 2, "power.networkId", 2),
            "force" => Change(rows[index], "force", "other"),
            _ => rows[index]
        };
        if (fault == "missing-belt") rows.RemoveAt(index);
        if (fault == "foreign-extractor") rows.Add(Entity("foreign", "inserter", new { pickupTargetId = "b0" }));
        snapshot = snapshot with { Records = rows };
        Assert.False(FactoryTransportHealth.Healthy(state, snapshot, bus));
        Assert.Empty(FactoryTransportHealth.Connected(state, snapshot));
    }

    [Fact]
    public void DemandPauseRequiresAnObservedZeroThreshold()
    {
        var (state, snapshot, bus) = Fixture();
        bus = bus with { Consumers = bus.Consumers.Select(c => c with { Paused = true }).ToArray() };
        state = state.With(bus);
        Assert.False(FactoryTransportHealth.Healthy(state, snapshot, bus));
        snapshot = snapshot with { Records = snapshot.Records.Select(r => r.EntityId.StartsWith("receiver-", StringComparison.Ordinal)
            ? Change(r, "transport.inserterControl.maximum", 0) : r).ToArray() };
        Assert.True(FactoryTransportHealth.Healthy(state, snapshot, bus));
    }

    [Theory]
    [InlineData("healthy", true)]
    [InlineData("wrong-comparison", false)]
    [InlineData("wrong-reserve", false)]
    [InlineData("foreign-source-wire", false)]
    [InlineData("missing-source-wire", false)]
    public void ActorReserveRequiresNativeSourceStockControl(string fault, bool healthy)
    {
        var (state, snapshot, bus) = Fixture();
        bus = bus with { ActorReserve = 25 };
        state = state.With(bus);
        snapshot = snapshot with { Records = snapshot.Records.Select(r => r.EntityId == "source-chest"
            ? Change(r, "transport.redNeighbours", fault == "foreign-source-wire" ? new[] { "extractor", "foreign" } : new[] { "extractor" },
                "transport.redNeighbourCount", fault == "foreign-source-wire" ? 2 : 1)
            : r.EntityId == "extractor" ? Change(r, "transport.inserterControl.circuitEnabled", true,
                "transport.inserterControl.circuitItem", "gear", "transport.inserterControl.comparator", fault == "wrong-comparison" ? "<" : ">",
                "transport.inserterControl.maximum", fault == "wrong-reserve" ? 0 : 25,
                "transport.inserterControl.redNeighbours", fault == "missing-source-wire" ? Array.Empty<string>() : new[] { "source-chest" },
                "transport.inserterControl.redNeighbourCount", fault == "missing-source-wire" ? 0 : 1) : r).ToArray() };
        Assert.Equal(healthy, FactoryTransportHealth.Healthy(state, snapshot, bus));
    }

    private static (FactoryState State, FactorySnapshot Snapshot, FactoryTransportBus Bus) Fixture()
    {
        var source = new FactoryCell("source", 1, new(0, 0, true), "assembler", "assembler", "gear",
            new Dictionary<string, string> { ["output-chest"] = "source-chest" }, "ready", 1);
        var first = source with { Id = "first", Entities = new Dictionary<string, string> { ["input-chest"] = "first-chest" } };
        var second = first with { Id = "second", Entities = new Dictionary<string, string> { ["input-chest"] = "second-chest" } };
        var transport = source with { Id = "line", Kind = "transport", Recipe = null, Entities = new Dictionary<string, string>
            { ["source-inserter"] = "extractor", ["target-inserter-0"] = "receiver-0", ["target-inserter-1"] = "receiver-1", ["belt-0"] = "b0", ["belt-1"] = "b1" },
            Plan = new Dictionary<string, PlannedEntity> { ["belt-0"] = new("belt-0", "belt", new(0, 0), 4), ["belt-1"] = new("belt-1", "belt", new(1, 0), 4) } };
        var bus = new FactoryTransportBus("bus", "source", "gear", "line", [new("first", "target-inserter-0", 5), new("second", "target-inserter-1", 5)]);
        var state = new FactoryState(1, "world", [], [source, first, second, transport], Transports: [bus]);
        ObservedInserterControl Control(string? chest) => new(true, "whitelist", ["gear"], chest is not null, chest is null ? null : "gear",
            chest is null ? null : "<", chest is null ? null : 5, chest is null ? [] : [chest], chest is null ? 0 : 1, NativeNormalFilters: true);
        var snapshot = new FactorySnapshot("native", new("world", "session", "actor", 1, 1), 10, 20, Protocol.ToElement(new { atomic = true }),
        [Entity("source-chest", "container", new { redNeighbours = Array.Empty<string>(), redNeighbourCount = 0 }),
         Entity("first-chest", "container", new { redNeighbours = new[] { "receiver-0" }, redNeighbourCount = 1 }),
         Entity("second-chest", "container", new { redNeighbours = new[] { "receiver-1" }, redNeighbourCount = 1 }),
         Entity("extractor", "inserter", new { pickupTargetId = "source-chest", dropTargetId = "b0", inserterControl = Control(null) }),
         Entity("receiver-0", "inserter", new { pickupTargetId = "b0", dropTargetId = "first-chest", inserterControl = Control("first-chest") }),
         Entity("receiver-1", "inserter", new { pickupTargetId = "b1", dropTargetId = "second-chest", inserterControl = Control("second-chest") }),
         Entity("b0", "transport-belt", new { beltConnections = new ObservedBeltConnections([], ["b1"], 0, 1) }),
         Entity("b1", "transport-belt", new { beltConnections = new ObservedBeltConnections(["b0"], [], 1, 0) }), Entity("generator", "generator", new { })]);
        return (state, snapshot, bus);
    }

    private static FactoryRecord Entity(string id, string type, object transport) => new(id, "entity", id, type,
        Protocol.ToElement(new { role = "factory", type, force = "own", surfaceIndex = 1, electricNetworkId = 1,
            power = new { energy = 1000, networkId = 1 }, transport }));

    private static FactoryRecord Change(FactoryRecord record, params object[] changes)
    {
        var node = JsonNode.Parse(record.Data.GetRawText())!;
        for (int i = 0; i < changes.Length; i += 2)
        {
            string[] path = ((string)changes[i]).Split('.');
            var parent = node;
            foreach (string segment in path[..^1]) parent = parent[segment]!;
            parent[path[^1]] = JsonSerializer.SerializeToNode(changes[i + 1]);
        }
        return record with { Data = JsonSerializer.SerializeToElement(node) };
    }
}
