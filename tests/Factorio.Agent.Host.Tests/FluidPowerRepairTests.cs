using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FluidPowerRepairTests
{
    [Fact]
    public void AnAdoptedExtractorWithoutAPoleRoleUsesItsNearestStandingIslandLink()
    {
        var cell = Cell(FluidCellBuilder.ExtractorKind, "drill", ("link-0", "source-link", new(-10, 0)),
            ("link-1", "gone", new(10, 0)), ("link-2", "near", new(18, 0)), ("link-3", "far", new(13, 0)));
        var snapshot = Snapshot(Entity("consumer", "mining-drill", new(20, 0), 2), Entity("source-link", "electric-pole", new(-10, 0), 1),
            Entity("near", "electric-pole", new(18, 0), 2), Entity("far", "electric-pole", new(13, 0), 2));
        var target = FluidPowerRepair.Target(cell, snapshot)!.Value;
        Assert.Equal("near", target.Pole.EntityId);
        Assert.Equal(cell.Plan!["link-2"], target.Plan);
    }

    [Theory]
    [InlineData(FluidCellBuilder.ExtractorKind, "drill")]
    [InlineData(FluidCellBuilder.MachineKind, "machine")]
    public void AStandingCellPoleIsPreferredToItsLinks(string kind, string consumer)
    {
        var cell = Cell(kind, consumer, ("pole", "pole", new(10, 0)), ("link-0", "link", new(18, 0)));
        Assert.Equal("pole", FluidPowerRepair.Target(cell, Snapshot(Entity("consumer", "assembling-machine", new(20, 0), 2),
            Entity("pole", "electric-pole", new(10, 0), 2), Entity("link", "electric-pole", new(18, 0), 2)))!.Value.Pole.EntityId);
    }

    [Fact]
    public void AGeneratorConnectedConsumerNeedsNoRepairEvenWithZeroStoredEnergy()
    {
        var cell = Cell(FluidCellBuilder.MachineKind, "machine", ("pole", "pole", new(18, 0)));
        Assert.Null(FluidPowerRepair.Target(cell, Snapshot(Entity("consumer", "assembling-machine", new(20, 0), 1),
            Entity("pole", "electric-pole", new(18, 0), 1))));
    }

    [Theory]
    [InlineData("building", FluidCellBuilder.MachineKind)]
    [InlineData("abandoned", FluidCellBuilder.MachineKind)]
    [InlineData("ready", "assembler")]
    public void InterruptedAbandonedAndSolidCellsAreLeftToTheirOwnControllers(string status, string kind)
    {
        Assert.Null(FluidPowerRepair.Target(Cell(kind, "machine", ("pole", "pole", new(18, 0))) with { Status = status },
            Snapshot(Entity("consumer", "assembling-machine", new(20, 0), 2), Entity("pole", "electric-pole", new(18, 0), 2))));
    }

    [Fact]
    public void MissingConsumersAndWrongNetworkOrMovedPolesCannotStartARepair()
    {
        var cell = Cell(FluidCellBuilder.MachineKind, "machine", ("pole", "pole", new(18, 0)));
        Assert.Null(FluidPowerRepair.Target(cell, Snapshot(Entity("pole", "electric-pole", new(18, 0), 2))));
        Assert.Null(FluidPowerRepair.Target(cell, Snapshot(Entity("consumer", "assembling-machine", new(20, 0), 2),
            Entity("pole", "electric-pole", new(18, 0), 1))));
        Assert.Null(FluidPowerRepair.Target(cell, Snapshot(Entity("consumer", "assembling-machine", new(20, 0), 2),
            Entity("pole", "electric-pole", new(15, 0), 2))));
        Assert.Null(FluidPowerRepair.Target(cell with { Plan = null }, Snapshot(Entity("consumer", "assembling-machine", new(20, 0), 2),
            Entity("pole", "electric-pole", new(18, 0), 2))));
    }

    private static FactoryCell Cell(string kind, string consumer, params (string Role, string Id, MapPosition Position)[] poles) =>
        new("cell", 0, new(0, 0, true), kind, "chemical-plant", "plastic-bar",
            poles.ToDictionary(p => p.Role, p => p.Id).Append(new KeyValuePair<string, string>(consumer, "consumer")).ToDictionary(p => p.Key, p => p.Value),
            "ready", 10, Plan: poles.ToDictionary(p => p.Role, p => new PlannedEntity(p.Role, "small-electric-pole", p.Position, 0)));

    private static FactoryRecord Entity(string id, string type, MapPosition position, long network) =>
        new(id, "entity", id, id, Protocol.ToElement(new { role = "factory", type, position, electricNetworkId = network, surfaceIndex = 1, power = new { energy = 0, networkId = network } }));

    private static FactorySnapshot Snapshot(params FactoryRecord[] records) =>
        new("snapshot", new("world", "session", "actor", 1, 1), 10, 20, Protocol.ToElement(new { atomic = true }),
            [Entity("source", "generator", new(-20, 0), 1), .. records]);
}
