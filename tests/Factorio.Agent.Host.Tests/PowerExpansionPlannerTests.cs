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
