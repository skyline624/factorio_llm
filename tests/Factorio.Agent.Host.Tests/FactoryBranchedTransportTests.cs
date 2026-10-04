using System.Text.Json;
using System.Text.Json.Nodes;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryBranchedTransportTests
{
    [Fact]
    public void NativeSplitterWithoutAFilterOmitsThatOptionalJsonField()
    {
        var observed = JsonSerializer.Deserialize<ObservedSplitterControl>(
            "{\"inputPriority\":\"none\",\"outputPriority\":\"none\"}", Protocol.Json);
        Assert.Equal(new("none", "none", null), observed);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ObservedSplitterControl>("{}", Protocol.Json));
    }

    [Fact]
    public void ExactNativeSplitterGraphFeedsBothTerminalConsumers()
    {
        var (state, snapshot, bus) = Fixture();
        Assert.True(FactoryTransportHealth.Healthy(state, snapshot, bus));
        Assert.Equal(.5, FactoryTransportCoverage.BranchAllocation(state, snapshot, bus, "first"));
        Assert.Equal(.5, FactoryTransportCoverage.BranchAllocation(state, snapshot, bus, "second"));
    }

    [Theory]
    [InlineData("priority")]
    [InlineData("filter")]
    [InlineData("unknown-input")]
    [InlineData("unknown-output")]
    [InlineData("missing-splitter")]
    [InlineData("foreign-force")]
    [InlineData("foreign-extractor")]
    [InlineData("wrong-type")]
    [InlineData("missing-control")]
    [InlineData("pollution")]
    public void ForeignOrBiasedNativeBranchesPreserveActorFallback(string fault)
    {
        var (state, snapshot, bus) = Fixture();
        var rows = snapshot.Records.ToList();
        int index = rows.FindIndex(r => r.EntityId == "split");
        rows[index] = fault switch
        {
            "priority" => Change(rows[index], "transport.splitterControl.outputPriority", "left"),
            "filter" => Change(rows[index], "transport.splitterControl.filter", "copper-plate"),
            "unknown-input" => Change(rows[index], "transport.beltConnections.inputsCount", 2),
            "unknown-output" => Change(rows[index], "transport.beltConnections.outputsCount", 3),
            "foreign-force" => Change(rows[index], "force", "other"),
            "wrong-type" => Change(rows[index], "type", "transport-belt"),
            "missing-control" => Change(rows[index], "transport.splitterControl", null),
            _ => rows[index]
        };
        if (fault == "missing-splitter") rows.RemoveAt(index);
        if (fault == "foreign-extractor") rows.Add(Entity("foreign", "inserter", new { pickupTargetId = "split" }));
        if (fault == "pollution") rows.Add(new("split-transit", "transit", "split", "splitter",
            Protocol.ToElement(new { items = new Dictionary<string, int> { ["copper-plate"] = 1 } })));
        Assert.False(FactoryTransportHealth.Healthy(state, snapshot with { Records = rows }, bus));
    }

    [Fact]
    public void MissingPlanMembersAndConsumerPickupOnAnInteriorNodeAreRejected()
    {
        var (state, snapshot, bus) = Fixture();
        var graph = new Dictionary<string, FactoryConveyorEdges>(bus.Graph!);
        graph.Remove("belt-2");
        Assert.False(FactoryTransportHealth.Healthy(state, snapshot, bus with { Graph = graph }));
        snapshot = snapshot with { Records = snapshot.Records.Select(r => r.EntityId == "receiver-0"
            ? Change(r, "transport.pickupTargetId", "b0") : r).ToArray() };
        Assert.False(FactoryTransportHealth.Healthy(state, snapshot, bus));
    }

    [Fact]
    public void NativeHalvesDoNotBecomeProportionalToRecipeDemand()
    {
        var (state, snapshot, _) = Fixture();
        var shares = new Dictionary<string, double> { ["iron-gear-wheel"] = 6, ["electronic-circuit"] = 30 };
        var covered = FactoryTransportCoverage.Connected(state, snapshot, Catalogs.Early(), shares);
        Assert.Contains(("first-chest", "iron-plate"), covered); // 12/min, physically allotted 24/min.
        Assert.DoesNotContain(("second-chest", "iron-plate"), covered); // 30/min cannot be promised from a 24/min branch.
    }

    [Fact]
    public void APausedBranchDoesNotPromiseUnmeasuredSurplusToTheOther()
    {
        var (state, snapshot, bus) = Fixture();
        bus = bus with { Consumers = bus.Consumers.Select(c => c with { Paused = c.TargetCellId == "second" }).ToArray() };
        state = state.With(bus);
        snapshot = snapshot with { Records = snapshot.Records.Select(r => r.EntityId == "receiver-1"
            ? Change(r, "transport.inserterControl.maximum", 0) : r).ToArray() };
        Assert.True(FactoryTransportHealth.Healthy(state, snapshot, bus));
        Assert.Equal(.5, FactoryTransportCoverage.BranchAllocation(state, snapshot, bus, "first"));
        Assert.Equal(0, FactoryTransportCoverage.BranchAllocation(state, snapshot, bus, "second"));
    }

    private static (FactoryState State, FactorySnapshot Snapshot, FactoryTransportBus Bus) Fixture()
    {
        var source = new FactoryCell("source", 0, new(0, 0, true), "smelter", "stone-furnace", "iron-plate",
            new Dictionary<string, string> { ["output-chest"] = "source-chest" }, "ready", 1);
        var first = source with { Id = "first", Kind = "assembler", Recipe = "iron-gear-wheel", MachineItem = "assembling-machine-1",
            Entities = new Dictionary<string, string> { ["input-chest"] = "first-chest" } };
        var second = first with { Id = "second", Recipe = "electronic-circuit", Entities = new Dictionary<string, string> { ["input-chest"] = "second-chest" } };
        var transport = source with { Id = "line", Kind = "transport", Recipe = null,
            Entities = new Dictionary<string, string> { ["source-inserter"] = "extractor", ["target-inserter-0"] = "receiver-0",
                ["target-inserter-1"] = "receiver-1", ["belt-0"] = "b0", ["belt-1"] = "b1", ["belt-2"] = "b2", ["splitter-0"] = "split" },
            Plan = new Dictionary<string, PlannedEntity> { ["belt-0"] = new("belt-0", "transport-belt", new(.5, .5), 4),
                ["belt-1"] = new("belt-1", "transport-belt", new(2.5, .5), 4), ["belt-2"] = new("belt-2", "transport-belt", new(2.5, 1.5), 4),
                ["splitter-0"] = new("splitter-0", "splitter", new(1.5, 1), 4) } };
        var bus = new FactoryTransportBus("bus", "source", "iron-plate", "line",
            [new("first", "target-inserter-0", 40), new("second", "target-inserter-1", 40)], Graph: new Dictionary<string, FactoryConveyorEdges>
            { ["belt-0"] = new([], ["splitter-0"]), ["splitter-0"] = new(["belt-0"], ["belt-1", "belt-2"]),
                ["belt-1"] = new(["splitter-0"], []), ["belt-2"] = new(["splitter-0"], []) });
        var row = new ResourceRow(0, "smelter", "iron-plate", "iron-ore", new("burner-mining-drill", "iron-chest", "stone-furnace"), new(0, 0), 4, 4, 1, 48);
        var state = new FactoryState(1, "world", [], [source, first, second, transport], [row], Transports: [bus]);
        ObservedInserterControl Control(string? chest) => new(true, "whitelist", ["iron-plate"], chest is not null,
            chest is null ? null : "iron-plate", chest is null ? null : "<", chest is null ? null : 40,
            chest is null ? [] : [chest], chest is null ? 0 : 1, NativeNormalFilters: true);
        var snapshot = new FactorySnapshot("native", Catalogs.Early().Scope, 10, 20, Protocol.ToElement(new { atomic = true }),
            [Entity("source-chest", "container", new { redNeighbours = Array.Empty<string>(), redNeighbourCount = 0 }),
             Entity("first-chest", "container", new { redNeighbours = new[] { "receiver-0" }, redNeighbourCount = 1 }),
             Entity("second-chest", "container", new { redNeighbours = new[] { "receiver-1" }, redNeighbourCount = 1 }),
             Entity("extractor", "inserter", new { pickupTargetId = "source-chest", dropTargetId = "b0", inserterControl = Control(null) }),
             Entity("receiver-0", "inserter", new { pickupTargetId = "b1", dropTargetId = "first-chest", inserterControl = Control("first-chest") }),
             Entity("receiver-1", "inserter", new { pickupTargetId = "b2", dropTargetId = "second-chest", inserterControl = Control("second-chest") }),
             Entity("b0", "transport-belt", new { beltConnections = new ObservedBeltConnections([], ["split"], 0, 1) }),
             Entity("b1", "transport-belt", new { beltConnections = new ObservedBeltConnections(["split"], [], 1, 0) }),
             Entity("b2", "transport-belt", new { beltConnections = new ObservedBeltConnections(["split"], [], 1, 0) }),
             Entity("split", "splitter", new { beltConnections = new ObservedBeltConnections(["b0"], ["b1", "b2"], 1, 2),
                 splitterControl = new ObservedSplitterControl("none", "none", null) }), Entity("generator", "generator", new { })]);
        return (state, snapshot, bus);
    }

    private static FactoryRecord Entity(string id, string type, object transport) => new(id, "entity", id, type,
        Protocol.ToElement(new { role = "factory", type, force = "own", surfaceIndex = 1, electricNetworkId = 1,
            power = new { energy = 1000, networkId = 1 }, transport }));

    private static FactoryRecord Change(FactoryRecord record, string path, object? value)
    {
        var node = JsonNode.Parse(record.Data.GetRawText())!;
        string[] segments = path.Split('.');
        var parent = node;
        foreach (string segment in segments[..^1]) parent = parent[segment]!;
        parent[segments[^1]] = JsonSerializer.SerializeToNode(value);
        return record with { Data = JsonSerializer.SerializeToElement(node) };
    }
}
