using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class AttackDetectionTests
{
    private static readonly IndustryCluster Smelting = new("cluster-7", new(new(-60, -10), new(-40, 10)), ["7", "8"]);
    private static readonly IndustryCluster Bands = new("cluster-120", new(new(10, 20), new(40, 45)), ["120", "zone-1"]);
    private static readonly IndustryCluster[] Clusters = [Smelting, Bands];
    private static AttackEvidence Evidence(DestroyedEntity[]? destroyed = null, DamagedEntity[]? damaged = null,
        VisibleThreat[]? enemies = null, ReflexEvent[]? reflexes = null) => new(destroyed ?? [], damaged ?? [], enemies ?? [], reflexes ?? []);

    [Fact]
    public void DestroyedAndDamagedEntitiesAreRecordedOnceForTheirCluster()
    {
        // Campaign 2026-10-01 (seed 20261002): packs hit factory areas far from the four turrets three times in 75 minutes.
        var evidence = Evidence([new("8", new(-45, 5))], [new("7", new(-55, -5), 120, 200), new("120", new(12, 22), 350, 350)]);
        var (records, memory) = AttackDetector.Detect(500, Clusters, evidence, AttackMemory.Empty);
        var record = Assert.Single(records);
        Assert.Equal(("cluster-7", 1, 1, 0, 500L), (record.Cluster, record.Destroyed, record.Damaged, record.Enemies, record.Tick));
        Assert.Null(record.Direction);
        Assert.Equal(new[] { "8", "7" }, record.Evidence);
        Assert.False(record.Responded);
        // The same destroyed id and an unchanged health are not a new attack; further damage is.
        Assert.Empty(AttackDetector.Detect(600, Clusters, evidence, memory).Records);
        var worse = Evidence([new("8", new(-45, 5))], [new("7", new(-55, -5), 80, 200)]);
        var again = Assert.Single(AttackDetector.Detect(700, Clusters, worse, memory).Records);
        Assert.Equal((0, 1), (again.Destroyed, again.Damaged));
    }

    [Theory]
    [InlineData(-30, 0, "E")]
    [InlineData(-50, -30, "N")]
    [InlineData(-75, 25, "SW")]
    public void VisibleEnemiesNearIndustryGiveTheAttackDirection(double x, double y, string direction)
    {
        var (records, memory) = AttackDetector.Detect(500, Clusters, Evidence(enemies: [new("biter-1", new(x, y)), new("biter-2", new(x, y + 1))]),
            AttackMemory.Empty);
        var record = Assert.Single(records);
        Assert.Equal(("cluster-7", 2, direction), (record.Cluster, record.Enemies, record.Direction));
        Assert.Empty(AttackDetector.Detect(510, Clusters, Evidence(enemies: [new("biter-1", new(x, y))]), memory).Records);
    }

    [Fact]
    public void EnemiesAndReflexFightsFarFromIndustryAreNotAttacksOnIt()
    {
        var far = Evidence(enemies: [new("biter", new(200, 200))], reflexes: [new(400, "shoot", new(150, 150), new(155, 150), 1)]);
        var (records, memory) = AttackDetector.Detect(500, Clusters, far, AttackMemory.Empty);
        Assert.Empty(records);
        Assert.Equal(400, memory.ReflexTick);
    }

    [Fact]
    public void ReflexFightsBesideAClusterAreCountedOnlyOnce()
    {
        // The actor fought a pack beside the bands: the reflex events are attack evidence even if the pack is gone.
        ReflexEvent[] fights = [new(300, "shoot", new(25, 48), new(26, 52), 3), new(320, "retreat", new(24, 47), new(25, 52), 3)];
        var (records, memory) = AttackDetector.Detect(500, Clusters, Evidence(reflexes: fights), AttackMemory.Empty);
        var record = Assert.Single(records);
        Assert.Equal(("cluster-120", 2, "S"), (record.Cluster, record.Reflexes, record.Direction));
        Assert.Contains(new MapPosition(26, 52), record.Points);
        Assert.Equal(320, memory.ReflexTick);
        Assert.Empty(AttackDetector.Detect(600, Clusters, Evidence(reflexes: fights), memory).Records);
    }

    [Fact]
    public void RepairedOrRebuiltEntitiesLeaveTheMemory()
    {
        var (_, memory) = AttackDetector.Detect(500, Clusters, Evidence([new("8", new(-45, 5))], [new("7", new(-55, -5), 120, 200)]), AttackMemory.Empty);
        var (_, healed) = AttackDetector.Detect(600, Clusters, Evidence(), memory);
        Assert.Empty(healed.Destroyed);
        Assert.Empty(healed.Health);
        // A later loss of the rebuilt entity is a new attack.
        Assert.Single(AttackDetector.Detect(700, Clusters, Evidence([new("8", new(-45, 5))]), healed).Records);
    }
}
