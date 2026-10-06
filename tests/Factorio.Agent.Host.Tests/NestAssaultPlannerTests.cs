using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class NestAssaultPlannerTests
{
    [Fact]
    public void LoadedTurretsReachTheNearestSpawnerFromAStandPointWithinBuildDistance()
    {
        var map = Map(Spawner("spawner", new(30.5, .5)));
        var step = new NestAssaultPlanner().Plan(map, "gun-turret", 4);
        Assert.NotNull(step);
        Assert.Equal("spawner", step.Target.Id);
        Assert.Equal(4, step.Turrets.Count);
        Assert.All(step.Turrets, t => Assert.True(t.Position.DistanceTo(step.Target.Position) <= 18 - 1.5));
        Assert.All(step.Turrets, t => Assert.True(t.Position.DistanceTo(step.Stand) <= map.Actor.BuildDistance));
        Assert.All(step.Turrets, a => Assert.All(step.Turrets.Where(b => b != a), b => Assert.True(a.Position.DistanceTo(b.Position) >= 2 - 1e-9)));
        Assert.Equal(0, step.Exposure);
        Assert.True(step.Stand.DistanceTo(step.Target.Position) > step.Turrets.Min(t => t.Position.DistanceTo(step.Target.Position)));
    }

    [Fact]
    public void AStandPointKeepsOutOfAWormBehindTheSpawner()
    {
        var map = Map(Spawner("spawner", new(30.5, .5)), Worm("worm", new(42.5, .5)));
        var step = new NestAssaultPlanner().Plan(map, "gun-turret", 4);
        Assert.NotNull(step);
        Assert.Equal("spawner", step.Target.Id);
        Assert.Equal(0, step.Exposure);
        Assert.True(step.Stand.DistanceTo(new(42.5, .5)) > 25);
    }

    [Fact]
    public void AFrontWormIsTheFirstTargetWithTheShallowestReachableExposure()
    {
        // The actor stands far outside the worm's reach, as at a staging point.
        var map = Map(Worm("worm", new(22.5, .5)), Spawner("spawner", new(36.5, .5)));
        map = map with { Actor = map.Actor with { Position = new(-30, .5) }, Entities = map.Entities.Select(e => e.Id == map.Actor.Id
            ? e with { Position = new(-30, .5), Bounds = map.Prototypes["character"].CollisionBox.Translate(new(-30, .5)) } : e).ToArray() };
        var step = new NestAssaultPlanner().Plan(map, "gun-turret", 4);
        Assert.NotNull(step);
        Assert.Equal("worm", step.Target.Id);
        // Turrets reach no farther than 18 tiles and the character builds within 10: some depth is unavoidable, not a deep stand.
        Assert.InRange(step.Exposure, 0, 5);
    }

    [Fact]
    public void NoVisibleEnemyStructureMeansNoAssaultStep()
    {
        Assert.Null(new NestAssaultPlanner().Plan(Map(), "gun-turret", 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NestAssaultPlanner().Plan(Map(), "gun-turret", 5));
    }

    private static SpatialEntity Spawner(string id, MapPosition at) =>
        new(id, "biter-spawner", at, new(new(at.X - 2.2, at.Y - 2.2), new(at.X + 2.2, at.Y + 2.2)), 0, "enemy");

    private static SpatialEntity Worm(string id, MapPosition at) =>
        new(id, "small-worm-turret", at, new(new(at.X - .9, at.Y - .8), new(at.X + .9, at.Y + .8)), 0, "enemy");

    internal static SpatialSnapshot Map(params SpatialEntity[] enemies)
    {
        var map = FactoryMaps.Grass(48);
        var solid = map.Prototypes["iron-chest"].Mask;
        var prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
        {
            ["biter-spawner"] = new("biter-spawner", "unit-spawner", new(new(-2.2, -2.2), new(2.2, 2.2)), solid, 5, 5),
            ["small-worm-turret"] = new("small-worm-turret", "turret", new(new(-.9, -.8), new(.9, .8)), solid, 2, 2),
            ["gun-turret"] = new("gun-turret", "ammo-turret", new(new(-.7, -.7), new(.7, .7)), solid, 2, 2, AttackRange: 18)
        };
        var actor = new SpatialEntity(map.Actor.Id, "character", map.Actor.Position,
            prototypes["character"].CollisionBox.Translate(map.Actor.Position), 0, "agent");
        return map with
        {
            Prototypes = prototypes,
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["gun-turret"] = new("gun-turret", 50) },
            Entities = [actor, .. enemies],
            StationaryThreats = enemies.Where(e => e.Name.EndsWith("worm-turret", StringComparison.Ordinal))
                .Select(e => new StationaryThreat(e.Id, e.Position, 25, map.CollectedTick)).ToArray()
        };
    }
}
