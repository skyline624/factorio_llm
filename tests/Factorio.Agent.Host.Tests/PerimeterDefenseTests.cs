using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class PerimeterDefenseTests
{
    private static SpatialEntity At(string id, string name, double x, double y, double half) =>
        new(id, name, new(x, y), new(new(x - half, y - half), new(x + half, y + half)), 0, "factorio_agent");

    [Fact]
    public void CellsAndZonesFormTheCoreAndOnlyOwnedIndustryIsOptional()
    {
        var zone = new FactoryZone(1, new(-10, -4), 8, 3, 12);
        var state = new FactoryState(1, "world", [zone], [
            new("cell-a", 1, new(0, 0, true), "assembler", "assembling-machine-1", "iron-gear-wheel",
                new Dictionary<string, string> { ["machine"] = "machine-1", ["pole"] = "far-pole" }, "ready", 1),
            new("wall-a", 0, new(0, 0, true), "wall", "stone-wall", null, new Dictionary<string, string> { ["wall-0"] = "wall-1" }, "ready", 1),
            new("turret-a", 0, new(0, 1, true), "turret", "gun-turret", null, new Dictionary<string, string> { ["turret"] = "turret-1" }, "ready", 1)
        ]);
        var map = FactoryMaps.Grass(40, [
            At("machine-1", "assembling-machine-1", -8.5, -2.5, 1.2),
            At("wall-1", "stone-wall", 20.5, 20.5, .29),
            At("turret-1", "gun-turret", 18, 18, .7),
            At("furnace-1", "stone-furnace", 10, 10, .7),
            At("furnace-2", "stone-furnace", -20, 10, .7),
            At("pole-1", "small-electric-pole", 5.5, 5.5, .15),
            At("silo-1", "rocket-silo", 25.5, -20.5, 4.2),
            At("drill-1", "burner-mining-drill", -25, -25, .7)
        ]);
        var owned = new HashSet<string> { "machine-1", "wall-1", "turret-1", "furnace-1", "pole-1", "far-pole", "silo-1", "drill-1" };
        var targets = PerimeterDefenseController.Targets(state, owned, map);
        Assert.Equal(new[] { zone.Box, map.Entities[0].Bounds }, targets.Core);
        // Silos and drills are the industry a perimeter most needs to cover, as for deployed turrets.
        Assert.Equal(new[] { "furnace-1", "silo-1", "drill-1" }, targets.Optional.Select(e => e.Id));
        Assert.Equal(new WorldBox(new(-10, -4), new(14, 8)), zone.Box);
    }

    [Fact]
    public void EveryOwnedEntityOutsideTheRingIsUnprotectedWhateverItsType()
    {
        var ring = new WorldBox(new(-15, -12), new(15, 12));
        var owned = new Dictionary<string, MapPosition>
        {
            ["machine"] = new(0, 0), ["walkway-pole"] = new(14.5, 11.5), ["link-pole"] = new(30.5, 0.5),
            ["belt"] = new(-40.5, 3.5), ["registered-wall"] = new(16.5, 0.5), ["unregistered-turret"] = new(0, 20)
        };
        var unprotected = PerimeterDefenseController.Unprotected(owned, new HashSet<string> { "registered-wall" }, ring);
        Assert.Equal(new[] { "belt", "link-pole", "unregistered-turret" }, unprotected);
    }

    [Fact]
    public void OnlyUnbuiltOrDestroyedRolesOfThePlannedRingRemain()
    {
        var turret = new PlannedEntity("turret", "gun-turret", new(-10, -10), 0);
        PlannedEntity[] walls = [new("wall@-10.5,-12.5", "stone-wall", new(-10.5, -12.5), 0), new("wall@-9.5,-12.5", "stone-wall", new(-9.5, -12.5), 0)];
        var nest = new PerimeterNest(0, turret, walls, new(new(-11, -13), new(-8, -9)));
        var plan = new PerimeterPlan(new(new(-6, -5), new(6, 5)), new(new(-11, -10), new(11, 10)), [nest], 0, 0, [], 0, [], null, null, true, true);
        var empty = new FactoryState(1, "world", [], []);
        Assert.Equal(3, PerimeterDefenseController.Remaining(plan, empty, new HashSet<string>()).Count);

        var built = empty.With(new(PerimeterDefenseController.TurretCell(nest), 0, new(0, 0, true), "turret", "gun-turret", null,
                new Dictionary<string, string> { ["turret"] = "7" }, "ready", 1, new Dictionary<string, PlannedEntity> { ["turret"] = turret }))
            .With(new(PerimeterDefenseController.WallCell(nest), 0, new(0, 0, true), "wall", "stone-wall", null,
                new Dictionary<string, string> { [walls[0].Role] = "8", [walls[1].Role] = "9" }, "ready", 1, walls.ToDictionary(w => w.Role)));
        Assert.Empty(PerimeterDefenseController.Remaining(plan, built, new HashSet<string> { "7", "8", "9" }));
        Assert.True(PerimeterDefenseController.Complete(plan, built, new HashSet<string> { "7", "8", "9" }));
        // An interrupted pass leaves its cell unfinished even when every entity is present, so the ring is not complete.
        var interrupted = built.With(built.Cells.Single(c => c.Kind == "wall") with { Status = "building" });
        Assert.False(PerimeterDefenseController.Complete(plan, interrupted, new HashSet<string> { "7", "8", "9" }));
        Assert.False(PerimeterDefenseController.Complete(plan, built, new HashSet<string> { "7", "8" }));
        // A refused wall was never registered and a destroyed one is no longer present: both remain, nothing else.
        var refused = built.With(built.Cells.Single(c => c.Kind == "wall") with { Entities = new Dictionary<string, string> { [walls[0].Role] = "8" } });
        Assert.Equal(new[] { walls[1] }, PerimeterDefenseController.Remaining(plan, refused, new HashSet<string> { "7", "8" }).Select(r => r.Entity));
        Assert.Equal(new[] { walls[0] }, PerimeterDefenseController.Remaining(plan, built, new HashSet<string> { "7", "9" }).Select(r => r.Entity));
    }
}
