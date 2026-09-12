using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SteamPowerPlannerTests
{
    [Fact]
    public void RecoveryRejectsSwappedRolesBeforeReusingTheEquipment()
    {
        var map = Map(water: true);
        var plan = new SteamPowerPlanner().Find(map, Equipment)!;
        plan = plan with { Machines = plan.Machines.Select(m => m with
            { Role = m.Role == "pump" ? "boiler" : m.Role == "boiler" ? "pump" : m.Role }).ToArray() };
        Assert.Throws<InvalidDataException>(() => plan.ValidateRecovery(map.Scope, Equipment));
    }

    [Fact]
    public void RecoveryAllowsANewSessionInTheSameWorldButRejectsAnotherWorld()
    {
        var map = Map(water: true);
        var plan = new SteamPowerPlanner().Find(map, Equipment)!;
        plan.ValidateRecovery(map.Scope with { SessionId = "resumed" }, Equipment);
        Assert.Throws<InvalidDataException>(() => plan.ValidateRecovery(map.Scope with { WorldId = "other" }, Equipment));
    }

    [Fact]
    public void DryTerrainCannotBeAdvertisedAsSteamPower()
    {
        Assert.Null(new SteamPowerPlanner().Find(Map(water: false), Equipment));
    }

    [Fact]
    public void ShoreInstallationSeparatesWaterAndSteamAndSuppliesTheLoad()
    {
        var map = Map(water: true);
        var plan = new SteamPowerPlanner().Find(map, Equipment);
        Assert.NotNull(plan);
        Assert.Equal(5, plan.Machines.Count);
        Assert.Equal(1, plan.WaterConnection.SourceBox);
        Assert.Equal(1, plan.WaterConnection.TargetBox);
        Assert.Equal(2, plan.SteamConnection.SourceBox);
        Assert.Equal(1, plan.SteamConnection.TargetBox);
        var pump = plan.Machines.Single(m => m.Role == "pump");
        Assert.Equal(0.5, pump.Placement.Position.Y);
        Assert.Equal(0, pump.Placement.Direction);
        var pole = plan.Machines.Single(m => m.Role == "pole");
        var load = plan.Machines.Single(m => m.Role == "load");
        Assert.InRange(Math.Abs(load.Placement.Position.X - pole.Placement.Position.X), 0, 2.5);
        Assert.InRange(Math.Abs(load.Placement.Position.Y - pole.Placement.Position.Y), 0, 2.5);
    }

    internal static PowerEquipment Equipment => new("pump", "boiler", "engine", "pole", "load");

    internal static SpatialSnapshot Map(bool water)
    {
        var map = SpatialPlannerTests.Map([]);
        var solid = new CollisionMask(["object"], false, false, false);
        var wet = new CollisionMask(["water"], false, false, false);
        var ground = new CollisionMask(["ground"], false, false, false);
        MapPosition[] center = [new(0, 0), new(0, 0), new(0, 0), new(0, 0)];
        FluidPortGeometry Port(int index, int direction, string flow, MapPosition[] positions) => new(index, "normal", direction, flow, positions, ["default"]);
        EntityGeometry Small(string name) => new(name, name, new(new(-0.15, -0.15), new(0.15, 0.15)), solid, 1, 1);
        var pump = Small("pump") with
        {
            FluidSourceOffset = new(0, -1),
            FluidBoxes = [new(1, "output", [Port(1, 8, "output", center)])],
            TileBuildability = [new(new(new(-0.4, -0.4), new(0.4, 0.4)), wet, ground),
                new(new(new(-0.4, -1.4), new(0.4, -0.6)), ground, wet)]
        };
        var boiler = new EntityGeometry("boiler", "boiler", new(new(-1.29, -0.79), new(1.29, 0.79)),
            new(["object", "water"], false, false, false), 3, 2, FluidBoxes:
            [new(1, "input", [Port(1, 12, "input-output", [new(-1, .5), new(-.5, -1), new(1, -.5), new(.5, 1)])], "water"),
             new(2, "output", [Port(1, 0, "output", [new(0, -.5), new(.5, 0), new(0, .5), new(-.5, 0)])], "steam")]);
        var engine = new EntityGeometry("engine", "generator", new(new(-1.25, -2.35), new(1.25, 2.35)),
            boiler.Mask, 3, 5, FluidBoxes: [new(1, "input", [Port(1, 8, "input-output", [new(0, 2), new(-2, 0), new(0, -2), new(2, 0)])], "steam")]);
        var prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
        {
            ["pump"] = pump,
            ["boiler"] = boiler,
            ["engine"] = engine,
            ["pole"] = Small("pole") with { SupplyArea = 2.5 },
            ["load"] = Small("load")
        };
        return map with
        {
            Actor = map.Actor with { Position = new(.5, 6.5) },
            Prototypes = prototypes,
            TilePrototypes = new Dictionary<string, CollisionMask> { ["grass"] = ground, ["water"] = wet },
            TileFluids = new Dictionary<string, string> { ["water"] = "water" },
            Rows = map.Rows.Select(r => r with { Name = water && r.Y < 0 ? "water" : "grass" }).ToArray(),
            Items = Equipment.Items.ToDictionary(i => i, i => new PlaceableItem(i, 50))
        };
    }
}
