using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class CliffMemoryTests
{
    [Fact]
    public void SeenCliffsStayRememberedUntilAPhotographCoveringThemNoLongerShowsThem()
    {
        // Seed 987654321, 2026-10-08: each new trip from a cliff-walled valley started without the cliffs of earlier trips.
        var map = Map([Cliff("a", 2.5, 2.5), Cliff("b", 6.5, 2.5)]);
        var memory = CliffMemory.Empty("world").Observe(map);
        Assert.Equal(["a", "b"], memory.Cliffs.Keys.Order());
        // A photograph elsewhere keeps them; one covering "b" without it forgets "b" only.
        var elsewhere = map with { Bounds = new(new(100, 100), new(120, 120)), Entities = [] };
        Assert.Equal(2, memory.Observe(elsewhere).Cliffs.Count);
        var destroyed = memory.Observe(Map([Cliff("a", 2.5, 2.5)]));
        Assert.Equal(["a"], destroyed.Cliffs.Keys);
        Assert.Equal([new MapPosition(2.5, 2.5)], destroyed.Near(new(0, 0), new(1, 1), 4));
        Assert.Empty(destroyed.Near(new(50, 50), new(60, 60), 4));
        Assert.Throws<InvalidDataException>(() => CliffMemory.Empty("other").Observe(map));
    }

    private static SpatialEntity Cliff(string id, double x, double y) => new(id, "cliff", new(x, y), new(new(x - 2, y - 1), new(x + 2, y + 1)), 0, "neutral");

    private static SpatialSnapshot Map(SpatialEntity[] cliffs)
    {
        var map = SpatialPlannerTests.Map([]);
        return map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["cliff"] = new("cliff", "cliff", new(new(-2, -1), new(2, 1)), map.Prototypes["wall"].Mask, 4, 2) },
            Entities = cliffs
        };
    }
}
