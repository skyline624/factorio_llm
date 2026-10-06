using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class NestMemoryTests
{
    [Fact]
    public void SightingsPersistOutsideLaterViewsAndVanishWhenAViewCoversThemAgain()
    {
        var spawner = new SpatialEntity("s1", "biter-spawner", new(30.5, .5), new(new(28.3, -1.7), new(32.7, 2.7)), 0, "enemy");
        var worm = new SpatialEntity("w1", "small-worm-turret", new(36.5, 4.5), new(new(35.6, 3.7), new(37.4, 5.3)), 0, "enemy");
        var memory = NestMemory.Empty("world").Observe(NestAssaultPlannerTests.Map(spawner, worm));
        Assert.Equal(["s1", "w1"], memory.Structures.Select(s => s.Id));
        Assert.Equal(25, memory.Structures.Single(s => s.Id == "w1").Range);

        // A later view elsewhere keeps both; a view covering them without the spawner forgets only the spawner.
        var away = NestAssaultPlannerTests.Map() with { Bounds = new(new(-200, -200), new(-150, -150)) };
        Assert.Equal(2, memory.Observe(away).Structures.Count);
        var cleared = memory.Observe(NestAssaultPlannerTests.Map(worm));
        Assert.Equal(["w1"], cleared.Structures.Select(s => s.Id));
    }

    [Fact]
    public void NearbyStructuresFormOneClusterAndDistantOnesAnother()
    {
        var memory = new NestMemory(1, "world", [
            new("a", "biter-spawner", new(0, 0), null, 1), new("b", "biter-spawner", new(10, 0), null, 1),
            new("c", "small-worm-turret", new(30, 0), 25, 1), new("d", "biter-spawner", new(200, 0), null, 1)]);
        var clusters = memory.Clusters().OrderBy(c => c.Center.X).ToArray();
        Assert.Equal(2, clusters.Length);
        Assert.Equal(2, clusters[0].Spawners);
        Assert.Equal(1, clusters[0].Worms);
        Assert.Equal(1, clusters[1].Spawners);
    }

    [Fact]
    public void AnotherWorldsPhotographIsRejected()
    {
        var map = NestAssaultPlannerTests.Map();
        Assert.Throws<InvalidDataException>(() => NestMemory.Empty("other").Observe(map));
    }
}
