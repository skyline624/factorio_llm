using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class PowerExpansionPlannerTests
{
    private const string Force = "agent";

    [Fact]
    public void NativeHeatAndOutputGiveTwoEnginesPerBoiler()
    {
        var map = FactoryMaps.Grass(8);
        Assert.Equal(2, PowerExpansionPlanner.EnginesPerBoiler(map.Prototypes["boiler"], map.Prototypes["steam-engine"]));
        Assert.Throws<InvalidDataException>(() => PowerExpansionPlanner.EnginesPerBoiler(map.Prototypes["boiler"],
            map.Prototypes["steam-engine"] with { MaxPowerOutput = null }));
    }

    [Fact]
    public void ChainsFollowObservedSteamConnectionsOnly()
    {
        var unconnected = Entity("loose", "steam-engine", new(4.5, 12.5), 4);
        var map = Shore([.. FirstSupply(), unconnected]);
        var chain = Assert.Single(PowerExpansionPlanner.Chains(map, Force));
        Assert.Equal("boiler1", chain.Boiler.Id);
        Assert.Equal(["engine1"], chain.Engines.Select(e => e.Id));
    }

    [Fact]
    public void ALoneBoilerIsCompletedByChainingAnEngineOnTheFreeSteamPort()
    {
        var map = Shore(FirstSupply());
        var plan = new PowerExpansionPlanner().Next(map, "boiler", "steam-engine", Force)!;
        Assert.Equal("complete", plan.Kind);
        Assert.Equal("boiler1", plan.BoilerId);
        var engine = Assert.Single(plan.Machines);
        Assert.Equal(new MapPosition(9.5, 2.5), engine.Placement.Position);
        var link = Assert.Single(plan.Links);
        Assert.Equal(("engine1", engine.Role), (link.Source, link.Target));
        Assert.Equal(new MapPosition(6.5, 2.5), link.Connection.SourcePosition);
        Assert.Equal(new MapPosition(7.5, 2.5), link.Connection.TargetPosition);
        AssertBuildable(map, plan);
    }

    [Fact]
    public void ACompleteBoilerGrowsANewBoilerFromItsFreeWaterPortWithTwoEngines()
    {
        var map = Shore(CompleteSupply());
        var plan = new PowerExpansionPlanner().Next(map, "boiler", "steam-engine", Force)!;
        Assert.Equal("unit", plan.Kind);
        Assert.Equal(["boiler", "engine-1", "engine-2"], plan.Machines.Select(m => m.Role));
        Assert.Equal(("boiler1", "boiler"), (plan.Links[0].Source, plan.Links[0].Target));
        // Water enters through the existing boiler's unused south port, never through the pump side.
        Assert.Equal(new MapPosition(0.5, 3.5), plan.Links[0].Connection.SourcePosition);
        Assert.Equal(new MapPosition(0.5, 4.5), plan.Links[0].Connection.TargetPosition);
        Assert.Equal(["boiler", "engine-1"], plan.Links.Skip(1).Select(l => l.Source));
        Assert.Equal(["engine-1", "engine-2"], plan.Links.Skip(1).Select(l => l.Target));
        AssertBuildable(map, plan);
    }

    [Fact]
    public void NoPlanWhenTheOnlyFreeWaterPortFacesWater()
    {
        var map = Shore(CompleteSupply(), water: (_, y) => y < 0 || y >= 4);
        Assert.Null(new PowerExpansionPlanner().Next(map, "boiler", "steam-engine", Force));
    }

    [Fact]
    public void ReservedGrowthKeepsFeedersOutOfTheNextUnit()
    {
        // Trees behind the boiler push the nearest feeder in front of the free water port, where the next boiler goes.
        var trees = new[] { 1.5, 2.5, 3.5 }.Select(y => new SpatialEntity($"tree{y}", "tree", new(-0.5, y), new(new(-0.9, y - .4), new(-0.1, y + .4)), 0, "neutral"));
        var map = Shore([.. CompleteSupply(), .. trees]);
        var planner = new PowerExpansionPlanner();
        var next = planner.Next(map, "boiler", "steam-engine", Force)!;
        var footprints = next.Machines.Select(m => Box(map, m.Item, m.Placement)).ToArray();
        bool Blocks(FuelFeederPlan feeder) => footprints.Any(f => f.Overlaps(Box(map, "iron-chest", feeder.Container))
            || f.Overlaps(Box(map, "inserter", feeder.Inserter)) || feeder.Pole is { } pole && f.Overlaps(Box(map, "small-electric-pole", pole)));
        Assert.True(Blocks(new FuelFeederPlanner().Find(map, "iron-chest", "inserter", "boiler1", 1, "small-electric-pole")!));

        var reserved = planner.ReserveGrowth(map, "boiler", "steam-engine", Force);
        Assert.Equal(3, reserved.Entities.Count - map.Entities.Count);
        var feeder = new FuelFeederPlanner().Find(reserved, "iron-chest", "inserter", "boiler1", 1, "small-electric-pole")!;
        Assert.False(Blocks(feeder));
        Assert.True(map.Entities.Single(e => e.Id == "boiler1").Bounds.Contains(feeder.Drop));
    }

    [Fact]
    public void BoilersWithoutAnObservedWaterSupplyAreNeverGrown()
    {
        // A stray boiler (aborted build) sorts before the installation; growing it would build engines that never run.
        var stray = Entity("a-stray", "boiler", new(-9.5, 10), 0);
        var map = Shore([.. CompleteSupply(), stray]);
        Assert.Equal(["boiler1"], PowerExpansionPlanner.Chains(map, Force).Select(c => c.Boiler.Id));
        var plan = new PowerExpansionPlanner().Next(map, "boiler", "steam-engine", Force)!;
        Assert.Equal(("unit", "boiler1"), (plan.Kind, plan.BoilerId));
        Assert.DoesNotContain(new PowerExpansionPlanner().Growth(map, "boiler", "steam-engine", Force, 2), e => e.Id.Contains("a-stray"));

        // Without its offshore pump, even the installation's boiler receives no water.
        var dry = Shore(CompleteSupply().Where(e => e.Id != "pump").ToArray());
        Assert.Empty(PowerExpansionPlanner.Chains(dry, Force));
        Assert.Null(new PowerExpansionPlanner().Next(dry, "boiler", "steam-engine", Force));
    }

    [Fact]
    public void AnInterruptedUnitIsCompletedFromItsWateredBoiler()
    {
        var map = Shore(CompleteSupply());
        var unit = new PowerExpansionPlanner().Next(map, "boiler", "steam-engine", Force)!;
        var interrupted = Build(map, unit with { Machines = unit.Machines.Take(2).ToArray(), Links = unit.Links.Take(2).ToArray() }, "u1");
        var plan = new PowerExpansionPlanner().Next(interrupted, "boiler", "steam-engine", Force)!;
        Assert.Equal(("complete", "u1:boiler"), (plan.Kind, plan.BoilerId));
        var engine = Assert.Single(plan.Machines);
        Assert.Equal(unit.Machines[2].Placement.Position, engine.Placement.Position);
        Assert.Equal("u1:engine-1", Assert.Single(plan.Links).Source);
    }

    [Theory]
    [InlineData(-15)]
    [InlineData(15)]
    public void NewUnitsAlternateSidesWhereverTheActorStands(double actorX)
    {
        var map = Shore(CompleteSupply());
        map = map with { Actor = map.Actor with { Position = new(actorX, 6) } };
        var plan = new PowerExpansionPlanner().Next(map, "boiler", "steam-engine", Force)!;
        // boiler1 faces east with its engines; the new boiler faces west so each engine row keeps a free neighbour row.
        Assert.Equal((new MapPosition(0, 5.5), 12), (plan.Machines[0].Placement.Position, plan.Machines[0].Placement.Direction));
        Assert.All(plan.Machines.Skip(1), m => Assert.True(m.Placement.Position.X < 0, m.ToString()));
    }

    [Fact]
    public void ReservedGrowthIsTheSequenceOfUnitsLaterStepsBuild()
    {
        var map = Shore(CompleteSupply());
        var planner = new PowerExpansionPlanner();
        var growth = planner.Growth(map, "boiler", "steam-engine", Force, 3);
        Assert.Equal(9, growth.Count);
        for (int unit = 0; unit < 3; unit++)
        {
            var plan = planner.Next(map, "boiler", "steam-engine", Force)!;
            Assert.Equal("unit", plan.Kind);
            Assert.Equal(growth.Skip(3 * unit).Take(3).Select(e => e.Bounds), plan.Machines.Select(m => Box(map, m.Item, m.Placement)));
            map = Build(map, plan, $"u{unit}");
        }
    }

    [Fact]
    public void AnUnderEquippedBoilerReservesItsMissingEnginesBeforeNewUnits()
    {
        var map = Shore(FirstSupply());
        var complete = new PowerExpansionPlanner().Next(map, "boiler", "steam-engine", Force)!;
        var growth = new PowerExpansionPlanner().Growth(map, "boiler", "steam-engine", Force, 1);
        Assert.Equal(4, growth.Count);
        Assert.Equal(Box(map, "steam-engine", Assert.Single(complete.Machines).Placement), growth[0].Bounds);
    }

    [Fact]
    public void FactoryBandsLeaveRoomForSeveralFutureUnits()
    {
        var map = FactoryMaps.Grass(40, CompleteSupply(), (_, y) => y < 0 ? "water" : "grass");
        var machine = map.Prototypes["assembling-machine-1"];
        var pole = map.Entities.Single(e => e.Id == "pole").Position;
        var planner = new PowerExpansionPlanner();
        var next = planner.Next(map, "boiler", "steam-engine", Force)!;
        WorldBox Band(FactoryZoneSite site) => new(site.Origin, new(site.Origin.X + site.Slots * FactoryBandPlanner.Pitch(machine),
            site.Origin.Y + FactoryBandPlanner.BandHeight(machine)));
        // Nearest to the network, an unreserved band lands on the only free water port.
        var raw = new FactoryZonePlanner().Find(map, machine, pole, FactoryCellBuilder.ZoneSlots)!;
        Assert.Contains(next.Machines, m => Box(map, m.Item, m.Placement).Overlaps(Band(raw)));

        var steam = new PowerExpansionController.SteamItems("boiler", "steam-engine", "small-electric-pole", "iron-chest", "inserter");
        const int units = 6;
        var site = new FactoryZonePlanner().Find(PowerExpansionController.ReserveGrowth(map, steam, [], Force, units), machine, pole,
            FactoryCellBuilder.ZoneSlots)!;
        var zone = new FactoryZone(1, site.Origin, site.Slots, FactoryBandPlanner.Pitch(machine), FactoryBandPlanner.BandHeight(machine));
        for (int unit = 0; unit < units; unit++)
        {
            var plan = planner.Next(FactoryCellBuilder.ReserveZone(map, zone, steam.Pole), "boiler", "steam-engine", Force);
            Assert.Equal("unit", plan?.Kind);
            Assert.DoesNotContain(plan!.Machines, m => Box(map, m.Item, m.Placement).Overlaps(Band(site)));
            map = Build(map, plan, $"u{unit}");
        }
    }

    [Fact]
    public void GrowthReservedOverAnAlreadyReservedBandCountsTheBandOnce()
    {
        // A band's power link reserves its own band, then the steam growth planned with every registered band.
        var map = FactoryMaps.Grass(40, CompleteSupply(), (_, y) => y < 0 ? "water" : "grass");
        var zone = new FactoryZone(1, new(14, 1), 8, 3, 12);
        var steam = new PowerExpansionController.SteamItems("boiler", "steam-engine", "small-electric-pole", "iron-chest", "inserter");
        var reserved = FactoryCellBuilder.ReserveZone(map, zone, steam.Pole);
        Assert.Same(reserved, FactoryCellBuilder.ReserveZone(reserved, zone, steam.Pole));
        var both = PowerExpansionController.ReserveGrowth(reserved, steam, [zone], Force, 2);
        Assert.Single(both.Entities, e => e.Id == "factory-zone-reservation:1");
        Assert.Equal(6, both.Entities.Count - reserved.Entities.Count);
    }

    /// <summary>The observation after a plan is built: planned machines become entities whose native ports connect as planned.</summary>
    private static SpatialSnapshot Build(SpatialSnapshot map, SteamExpansionPlan plan, string prefix)
    {
        var entities = map.Entities.ToList();
        string Id(string reference) => plan.Machines.Any(m => m.Role == reference) ? $"{prefix}:{reference}" : reference;
        foreach (var machine in plan.Machines)
            entities.Add(Entity(Id(machine.Role), machine.Item, machine.Placement.Position, machine.Placement.Direction));
        foreach (var (source, target, c) in plan.Links)
        {
            Connect(Id(source), Link(c.SourceBox, c.SourcePort, c.SourcePosition, c.TargetPosition, Id(target), c.TargetBox));
            Connect(Id(target), Link(c.TargetBox, c.TargetPort, c.TargetPosition, c.SourcePosition, Id(source), c.SourceBox));
        }
        return map with { Entities = entities };

        void Connect(string id, ObservedFluidConnection link)
        {
            int index = entities.FindIndex(e => e.Id == id);
            var kept = (entities[index].FluidConnections ?? []).Where(c => c.Position != link.Position || c.TargetPosition != link.TargetPosition);
            entities[index] = entities[index] with { FluidConnections = [.. kept, link] };
        }
    }

    private static void AssertBuildable(SpatialSnapshot map, SteamExpansionPlan plan)
    {
        foreach (var machine in plan.Machines)
        {
            var geometry = map.Prototypes[map.Items[machine.Item].EntityName];
            Assert.True(new SpatialCollisionField(map).PlacementClear(geometry, machine.Placement.Position, machine.Placement.Direction), machine.ToString());
            map = map with { Entities = [.. map.Entities, new("planned:" + machine.Role, geometry.Name, machine.Placement.Position,
                Box(map, machine.Item, machine.Placement), machine.Placement.Direction, Force)] };
        }
    }

    private static WorldBox Box(SpatialSnapshot map, string item, PlacementCandidate placement) =>
        map.Prototypes[map.Items[item].EntityName].CollisionBox.Rotate(placement.Direction).Translate(placement.Position);

    /// <summary>Water north of y=0; the observed first steam supply connects pump, boiler and engine through native ports.</summary>
    internal static SpatialSnapshot Shore(IReadOnlyList<SpatialEntity> entities, Func<int, int, bool>? water = null) =>
        FactoryMaps.Grass(20, entities, (x, y) => (water ?? ((_, row) => row < 0))(x, y) ? "water" : "grass");

    internal static SpatialEntity[] FirstSupply() =>
    [
        Entity("pump", "offshore-pump", new(0.5, 0.5), 0, Link(1, 1, new(0.5, 0.5), new(0.5, 1.5), "boiler1", 1)),
        Entity("boiler1", "boiler", new(1, 2.5), 4, Link(1, 1, new(0.5, 1.5), new(0.5, 0.5), "pump", 1),
            Link(1, 2, new(0.5, 3.5), new(0.5, 4.5)), Link(2, 1, new(1.5, 2.5), new(2.5, 2.5), "engine1", 1)),
        Entity("engine1", "steam-engine", new(4.5, 2.5), 4, Link(1, 1, new(2.5, 2.5), new(1.5, 2.5), "boiler1", 2),
            Link(1, 2, new(6.5, 2.5), new(7.5, 2.5))),
        Entity("pole", "small-electric-pole", new(4.5, 0.5), 0) with { Power = new(0, 1) }
    ];

    internal static SpatialEntity[] CompleteSupply()
    {
        var entities = FirstSupply();
        entities[2] = entities[2] with { FluidConnections = [entities[2].FluidConnections![0], Link(1, 2, new(6.5, 2.5), new(7.5, 2.5), "engine2", 1)] };
        return [.. entities, Entity("engine2", "steam-engine", new(9.5, 2.5), 4, Link(1, 1, new(7.5, 2.5), new(6.5, 2.5), "engine1", 1),
            Link(1, 2, new(11.5, 2.5), new(12.5, 2.5)))];
    }

    private static ObservedFluidConnection Link(int box, int port, MapPosition at, MapPosition target, string? id = null, int? targetBox = null) =>
        new(box, port, at, target, id, targetBox);

    private static SpatialEntity Entity(string id, string item, MapPosition position, int direction, params ObservedFluidConnection[] links)
    {
        var geometry = FactoryMaps.Grass(1).Prototypes[item];
        return new(id, item, position, geometry.CollisionBox.Rotate(direction).Translate(position), direction, Force,
            FluidConnections: links.Length == 0 ? null : links);
    }
}
