using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ObsoletePowerLinkTests
{
    [Fact]
    public void ACompleteReconnectedCellRetiresOnlyAbsentLinkPoles()
    {
        var (catalog, state, snapshot) = Scene();
        var lost = FactoryMaintenance.ObsoletePowerLinks(state, snapshot, catalog).Single();
        Assert.Equal("link-0", lost.Role);
        Assert.Equal("destroyed", lost.PreviousId);
        Assert.Equal(state.Cells.Single().Plan![lost.Role], lost.Plan);
        // The preserved native pole and its plan still support future reconnection.
        Assert.Equal("standing", state.Cells.Single().Entities["link-2"]);
        Assert.Single(FactoryMaintenance.Missing(state, FactoryMaintenance.Present(snapshot)));
    }

    [Theory]
    [InlineData("building")]
    [InlineData("abandoned")]
    [InlineData("power")]
    [InlineData("no-plan")]
    [InlineData("missing-machine")]
    [InlineData("missing-pole")]
    [InlineData("missing-chest")]
    [InlineData("missing-pipe-link")]
    [InlineData("unknown-link-item")]
    [InlineData("unknown-link-plan")]
    [InlineData("foreign-machine")]
    [InlineData("foreign-standing-link")]
    [InlineData("foreign-source")]
    [InlineData("unknown-network")]
    [InlineData("generator-free-network")]
    [InlineData("another-consumer-disconnected")]
    [InlineData("source-only")]
    [InlineData("non-atomic")]
    [InlineData("unknown-coverage")]
    [InlineData("other-world")]
    [InlineData("other-session")]
    [InlineData("older-photograph")]
    [InlineData("newer-cell")]
    public void UnprovenOrIncompleteCellsKeepTheirLostLinks(string fault)
    {
        var (catalog, state, snapshot) = Scene();
        var cell = state.Cells.Single();
        var entities = new Dictionary<string, string>(cell.Entities);
        var plans = new Dictionary<string, PlannedEntity>(cell.Plan!);
        var records = snapshot.Records.ToList();
        switch (fault)
        {
            case "building": case "abandoned": cell = cell with { Status = fault }; break;
            case "power": cell = cell with { Kind = "power" }; break;
            case "no-plan": cell = cell with { Plan = null }; break;
            case "missing-machine": records.RemoveAll(r => r.EntityId == "consumer"); break;
            case "missing-pole": records.RemoveAll(r => r.EntityId == "pole"); break;
            case "missing-chest": entities["output-chest"] = "gone-chest"; break;
            case "missing-pipe-link": plans["link-0"] = plans["link-0"] with { Item = "pipe" }; break;
            case "unknown-link-item": plans["link-0"] = plans["link-0"] with { Item = "unknown" }; break;
            case "unknown-link-plan": plans.Remove("link-0"); break;
            case "foreign-machine": Replace("consumer", "assembling-machine", 1, "enemy"); break;
            case "foreign-standing-link": Replace("standing", "electric-pole", 1, "enemy"); break;
            case "foreign-source": Replace("source", "generator", 1, "enemy"); break;
            case "unknown-network": Replace("consumer", "assembling-machine", null); break;
            case "generator-free-network": Replace("consumer", "assembling-machine", 2); break;
            case "another-consumer-disconnected":
                entities["input-inserter"] = "other-consumer";
                records.Add(Entity("other-consumer", "inserter", 2)); break;
            case "source-only": Replace("consumer", "generator", 1); break;
            case "non-atomic": snapshot = snapshot with { Coverage = Protocol.ToElement(new { atomic = false }) }; break;
            case "unknown-coverage": snapshot = snapshot with { Coverage = Protocol.ToElement(new { }) }; break;
            case "other-world": state = state with { WorldId = "other" }; break;
            case "other-session": snapshot = snapshot with { Scope = snapshot.Scope with { SessionId = "other" } }; break;
            case "older-photograph": snapshot = snapshot with { CollectedTick = catalog.CollectedTick - 1 }; break;
            case "newer-cell": cell = cell with { Tick = snapshot.CollectedTick + 1 }; break;
        }
        cell = cell with { Entities = entities, Plan = fault == "no-plan" ? null : plans };
        Assert.Empty(FactoryMaintenance.ObsoletePowerLinks(state.With(cell), snapshot with { Records = records }, catalog));

        void Replace(string id, string type, long? network, string role = "factory")
        {
            records.RemoveAll(r => r.EntityId == id);
            records.Add(Entity(id, type, network, role));
        }
    }

    [Fact]
    public void AnAdoptedExtractorNeedsNoDedicatedPoleRoleButAllConsumersMustBeFed()
    {
        var (catalog, state, snapshot) = Scene();
        var cell = state.Cells.Single();
        cell = cell with { Kind = FluidCellBuilder.ExtractorKind,
            Entities = cell.Entities.Where(p => p.Key != "pole").ToDictionary() };
        Assert.Single(FactoryMaintenance.ObsoletePowerLinks(state.With(cell), snapshot, catalog));
        snapshot = snapshot with { Records = snapshot.Records.Where(r => r.EntityId != "source").ToArray() };
        Assert.Empty(FactoryMaintenance.ObsoletePowerLinks(state.With(cell), snapshot, catalog));
    }

    private static (ProductionCatalog Catalog, FactoryState State, FactorySnapshot Snapshot) Scene()
    {
        var catalog = Catalogs.Raw() with { CollectedTick = 10 };
        var cell = new FactoryCell("cell", 0, new(0, 0, true), "assembler", "assembling-machine-1", "copper-cable",
            new Dictionary<string, string> { ["machine"] = "consumer", ["pole"] = "pole", ["link-0"] = "destroyed",
                ["link-2"] = "standing" }, "ready", 10,
            Plan: new Dictionary<string, PlannedEntity> { ["link-0"] = new("link-0", "small-electric-pole", new(7.5, .5), 0),
                ["link-2"] = new("link-2", "small-electric-pole", new(14.5, .5), 0) });
        var snapshot = new FactorySnapshot("native", catalog.Scope, 20, 3620, Protocol.ToElement(new { atomic = true }),
            [Entity("source", "generator", 1), Entity("consumer", "assembling-machine", 1), Entity("pole", "electric-pole", 1),
                Entity("standing", "electric-pole", 1)]);
        return (catalog, new(1, catalog.Scope.WorldId, [], [cell]), snapshot);
    }

    private static FactoryRecord Entity(string id, string type, long? network, string role = "factory") =>
        new(id, "entity", id, id, Protocol.ToElement(new { role, type, electricNetworkId = network, power = new { energy = 0, networkId = network } }));
}
