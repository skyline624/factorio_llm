using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class PerimeterDefenseTests
{
    [Fact]
    public void CellsAndZonesFormTheCoreAndOnlyOwnedIndustryIsOptional()
    {
        static SpatialEntity At(string id, string name, double x, double y, double half) =>
            new(id, name, new(x, y), new(new(x - half, y - half), new(x + half, y + half)), 0, "factorio_agent");
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
            At("pole-1", "small-electric-pole", 5.5, 5.5, .15)
        ]);
        var owned = new HashSet<string> { "machine-1", "wall-1", "turret-1", "furnace-1", "pole-1", "far-pole" };
        var targets = PerimeterDefenseController.Targets(state, owned, map);
        Assert.Equal(new[] { zone.Box, map.Entities[0].Bounds }, targets.Core);
        Assert.Equal(new[] { "furnace-1" }, targets.Optional.Select(e => e.Id));
        Assert.Equal(new[] { "far-pole" }, targets.Unobserved);
        Assert.Equal(new WorldBox(new(-10, -4), new(14, 8)), zone.Box);
    }
}
