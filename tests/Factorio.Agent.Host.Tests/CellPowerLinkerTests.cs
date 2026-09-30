using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class CellPowerLinkerTests
{
    private const string PoleItem = "small-electric-pole";
    private static readonly ActorScope Scope = new("world", "session", "actor", 1, 1);

    [Fact]
    public void LinksToARemoteCellStartFromTheFedPoleNearestToIt()
    {
        // Crude oil lies far from the steam engines: the cell's own island is nearer but carries no power.
        var known = new FactorySnapshot("snapshot", Scope, 100, 200, Protocol.ToElement(new { }), [
            Entity("engine", "generator", new(-100, 0), 1),
            Entity("steam-pole", "electric-pole", new(-98.5, .5), 1),
            Entity("far-pole", "electric-pole", new(-150.5, .5), 1),
            Entity("cell-pole", "electric-pole", new(20.5, .5), 2),
            Entity("island-link", "electric-pole", new(14.5, .5), 2),
            Entity("stray", "electric-pole", new(0.5, .5), null)]);
        Assert.Equal(new MapPosition(-98.5, .5), CellPowerLinker.NearestFedPole(known, new(20.5, .5)));
        Assert.Null(CellPowerLinker.NearestFedPole(new FactorySnapshot("empty", Scope, 1, 2, Protocol.ToElement(new { }), []), new(0, 0)));
    }

    [Fact]
    public void APumpjackFarBeyondTheCaptureIsLinkedOutwardFromTheGrid()
    {
        // 119 tiles separate the grid from the cell pole: no 48-tile capture around the cell ever shows a fed pole.
        var cellPole = new MapPosition(20.5, .5);
        var world = new List<SpatialEntity> { Pole("grid", new(-98.5, .5), 1), Pole("cell-pole", cellPole, 2) };
        var actor = new MapPosition(20, 3);
        int links = 0, travels = 0, step = 0;
        for (; step < CellPowerLinker.MaximumSteps; step++)
        {
            var known = Known(world);
            if (FactoryPower.IsFed(known, "cell-pole") == true) break;
            var next = CellPowerLinker.Plan(known, View(actor, world), "cell-pole", cellPole, PoleItem, [map => map]);
            if (next is null)
            {
                actor = CellPowerLinker.NearestFedPole(known, cellPole)!;
                travels++;
                continue;
            }
            Assert.Equal(PowerGridSearchStatus.Extension, next.Status);
            var at = next.Pole!.Position;
            // Each link stands within wire reach of the fed network it extends.
            Assert.Contains(world, e => e.Power?.NetworkId == 1 && e.Position.DistanceTo(at) <= 7.5);
            world.Add(Pole($"link-{links++}", at, 1));
            // The engine wires a new pole to the poles in its reach, so the cell's island joins the fed network.
            if (at.DistanceTo(cellPole) <= 7.5) world[1] = world[1] with { Power = new(0, 1) };
            actor = at;
        }
        Assert.True(FactoryPower.IsFed(Known(world), "cell-pole"));
        Assert.Equal(1, travels);
        Assert.InRange(links, 16, 20);
    }

    [Fact]
    public void LinksKeepOffReservedGroundWhileItLeavesAPathAndFallBackOtherwise()
    {
        // A planned band lies across the straight line from the grid to the cell.
        var cellPole = new MapPosition(15.5, .5);
        var world = new List<SpatialEntity> { Pole("grid", new(-15.5, .5), 1), Pole("cell-pole", cellPole, 2) };
        var map = View(new(0, 0), world);
        var band = new WorldBox(new(-12, -4), new(12, 5));
        var pole = map.Prototypes[PoleItem];
        Func<SpatialSnapshot, SpatialSnapshot> keepBand = m => FactoryGround.Reserve(m, [band], PoleItem);
        var around = CellPowerLinker.Plan(Known(world), map, "cell-pole", cellPole, PoleItem, [keepBand])!;
        Assert.Equal(PowerGridSearchStatus.Extension, around.Status);
        Assert.False(band.Overlaps(pole.CollisionBox.Translate(around.Pole!.Position)));
        // Ground reserved everywhere leaves no path: the next, looser view plans the link.
        Func<SpatialSnapshot, SpatialSnapshot> keepAll = m => FactoryGround.Reserve(m, [m.Bounds], PoleItem);
        Assert.Equal(PowerGridSearchStatus.NoObservedPath, CellPowerLinker.Plan(Known(world), map, "cell-pole", cellPole, PoleItem, [keepAll])!.Status);
        Assert.Equal(PowerGridSearchStatus.Extension, CellPowerLinker.Plan(Known(world), map, "cell-pole", cellPole, PoleItem, [keepAll, m => m])!.Status);
        // Only fed poles in view are sources: none here, so the linker must travel first.
        Assert.Null(CellPowerLinker.Plan(Known(world), View(new(60, 0), world), "cell-pole", cellPole, PoleItem, [m => m]));
    }

    /// <summary>A 48-tile capture around the actor, as the mod returns it, of a flat grass world.</summary>
    private static SpatialSnapshot View(MapPosition actor, IReadOnlyList<SpatialEntity> world)
    {
        var grass = FactoryMaps.Grass(1);
        int x0 = (int)Math.Floor(actor.X) - 48, y0 = (int)Math.Floor(actor.Y) - 48;
        var bounds = new WorldBox(new(x0, y0), new(x0 + 97, y0 + 97));
        return grass with
        {
            Bounds = bounds,
            Actor = grass.Actor with { Position = actor },
            Rows = Enumerable.Range(y0, 97).Select(y => new TileRun(x0, y, 97, "grass")).ToArray(),
            Entities = world.Where(e => bounds.Contains(e.Position)).ToArray(),
            Coverage = new(true, true, "current-character-local-area", 48)
        };
    }

    /// <summary>The factory photograph of the world: every pole known, with an injected source on network 1.</summary>
    private static FactorySnapshot Known(IReadOnlyList<SpatialEntity> world) => new("snapshot", Scope, 100, 200, Protocol.ToElement(new { }),
        [Entity("source", "electric-energy-interface", new(-100, 0), 1),
            .. world.Select(e => Entity(e.Id, "electric-pole", e.Position, e.Power?.NetworkId))]);

    private static SpatialEntity Pole(string id, MapPosition at, long network) =>
        new(id, PoleItem, at, new WorldBox(new(-.15, -.15), new(.15, .15)).Translate(at), 0, "agent", Power: new(0, network));

    private static FactoryRecord Entity(string id, string type, MapPosition position, long? network) => new(id, "entity", id, type,
        network is null ? Protocol.ToElement(new { role = "factory", type, position })
            : Protocol.ToElement(new { role = "factory", type, position, electricNetworkId = network }));
}
