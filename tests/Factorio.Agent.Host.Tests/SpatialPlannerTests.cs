using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SpatialPlannerTests
{
    private static readonly CollisionMask Solid = new(["player"], false, false, false);
    private static readonly CollisionMask Ground = new([], false, false, false);

    [Fact]
    public void Route_goes_around_wall_and_has_clear_continuous_segments()
    {
        var field = new SpatialCollisionField(Map([Wall(new(new(1.5, -6), new(2.5, 6)))]));
        RoutePlan plan = new RoutePlanner().Find(field, new(8, 0), timeBudget: TimeSpan.FromSeconds(2));
        Assert.Equal(RouteStatus.Found, plan.Status);
        Assert.Contains(plan.Waypoints, p => Math.Abs(p.Y) > 6);
        Assert.True(plan.Length > 8);
        MapPosition previous = field.Map.Actor.Position;
        foreach (MapPosition next in plan.Waypoints) { Assert.True(field.SegmentClear(previous, next)); previous = next; }
        Assert.Equal(new MapPosition(8, 0), plan.Waypoints[^1]);
    }

    [Fact]
    public void Actor_at_native_contact_can_escape_without_smoothing_through_the_obstacle()
    {
        var field = new SpatialCollisionField(Map([Wall(new(new(0.2, -2), new(1.2, 2)))]));
        Assert.False(field.Walkable(new(0, 0)));
        Assert.True(field.Walkable(new(0, 0), 0));
        Assert.False(field.SegmentClear(new(0, 0), new(1, 0), 0));
        RoutePlan plan = new RoutePlanner().Find(field, new(5, 0), timeBudget: TimeSpan.FromSeconds(2));
        Assert.Equal(RouteStatus.Found, plan.Status);
        Assert.True(plan.UsesTightStartConnector);
        Assert.True(field.SegmentClear(new(0, 0), plan.Waypoints[0], 0));
        Assert.True(field.Walkable(plan.Waypoints[0]));
        for (int i = 1; i < plan.Waypoints.Count; i++)
            Assert.True(field.SegmentClear(plan.Waypoints[i - 1], plan.Waypoints[i]));
        var penetrating = new SpatialCollisionField(Map([Wall(new(new(0.19, -2), new(1.2, 2)))]));
        Assert.Equal(RouteStatus.StartBlocked, new RoutePlanner().Find(penetrating, new(5, 0)).Status);
    }

    [Fact]
    public void Diagonal_does_not_clip_an_obstacle_corner()
    {
        var field = new SpatialCollisionField(Map([Wall(new(new(0.6, -1), new(2, 0.6)))]));
        Assert.False(field.SegmentClear(new(0, 0), new(1.5, 1.5)));
    }

    [Fact]
    public void Exhaustion_is_distinct_from_search_budget_and_unknown_space()
    {
        var field = new SpatialCollisionField(Map([Wall(new(new(1.5, -12), new(2.5, 13)))]));
        var planner = new RoutePlanner();
        Assert.Equal(RouteStatus.BudgetExceeded, planner.Find(field, new(8, 0), maximumNodes: 1).Status);
        Assert.Equal(RouteStatus.GoalOutsideSnapshot, planner.Find(field, new(30, 0)).Status);
        Assert.Equal(RouteStatus.NoRouteOnKnownGrid, planner.Find(field, new(8, 0),
            timeBudget: TimeSpan.FromSeconds(5)).Status);
    }

    [Fact]
    public void Character_tile_transition_uses_center_but_buildings_use_full_footprint()
    {
        SpatialSnapshot map = Map([]);
        var rows = map.Rows.Where(r => r.Y != 0).Concat(new[]
        {
            new TileRun(-12, 0, 12, "grass"), new TileRun(0, 0, 1, "water"), new TileRun(1, 0, 12, "grass")
        }).ToArray();
        map = map with { Rows = rows };
        var field = new SpatialCollisionField(map);
        Assert.True(field.Walkable(new(-0.1, 0.5), clearance: 0));
        Assert.False(field.Walkable(new(0.1, 0.5), clearance: 0));
        Assert.False(field.PlacementClear(map.Prototypes["furnace"], new(0, 0), 0));
    }

    [Fact]
    public void Odd_even_dimensions_and_rotation_obey_native_tile_alignment()
    {
        var field = new SpatialCollisionField(Map([]));
        var planner = new PlacementPlanner();
        IReadOnlyList<PlacementCandidate> furnaces = planner.FindCandidates(field, "furnace", new(5, 0));
        Assert.Equal(new MapPosition(5, 0), furnaces[0].Position);
        Assert.All(furnaces, c => { Assert.Equal(Math.Round(c.Position.X), c.Position.X); Assert.Equal(Math.Round(c.Position.Y), c.Position.Y); });
        IReadOnlyList<PlacementCandidate> chests = planner.FindCandidates(field, "chest", new(5, 0));
        Assert.All(chests, c => { Assert.Equal(0.5, Math.Abs(c.Position.X % 1)); Assert.Equal(0.5, Math.Abs(c.Position.Y % 1)); });
        IReadOnlyList<PlacementCandidate> pumps = planner.FindCandidates(field, "pump", new(4, 0));
        Assert.Contains(pumps, c => c.Direction == 4);
        foreach (PlacementCandidate c in pumps.Where(c => c.Direction == 4))
        {
            Assert.Equal(Math.Round(c.Position.X), c.Position.X);
            Assert.Equal(0.5, Math.Abs(c.Position.Y % 1));
        }
    }

    [Fact]
    public void Native_same_mask_exclusion_does_not_apply_to_tiles()
    {
        var a = new CollisionMask(["object", "player"], false, true, false);
        var b = new CollisionMask(["player", "object"], false, true, false);
        Assert.False(a.CollidesWith(b, tile: false));
        Assert.True(a.CollidesWith(b, tile: true));
        Assert.True(a.CollidesWith(b with { IgnoreSameMask = false }, tile: false));
    }

    [Fact]
    public void Missing_terrain_is_rejected_instead_of_assumed_walkable()
    {
        SpatialSnapshot map = Map([]);
        Assert.Throws<InvalidDataException>(() => new SpatialCollisionField(map with { Rows = map.Rows.Skip(1).ToArray() }));
    }

    private static SpatialEntity Wall(WorldBox box) => new("wall", "wall", new(2, 0), box, 0, "agent");

    internal static SpatialSnapshot Map(IReadOnlyList<SpatialEntity> entities)
    {
        var prototypes = new Dictionary<string, EntityGeometry>
        {
            ["character"] = new("character", "character", new(new(-0.2, -0.2), new(0.2, 0.2)),
                Solid with { TileTransitions = true }, 1, 1),
            ["wall"] = new("wall", "wall", new(new(-0.5, -0.5), new(0.5, 0.5)), Solid, 1, 1),
            ["furnace"] = new("furnace", "furnace", new(new(-0.7, -0.7), new(0.7, 0.7)), Solid, 2, 2),
            ["chest"] = new("chest", "container", new(new(-0.35, -0.35), new(0.35, 0.35)), Solid, 1, 1),
            ["pump"] = new("pump", "pump", new(new(-0.3, -0.8), new(0.3, 0.8)), Solid, 1, 2)
        };
        return new(new("world", "session", "actor", 1, 2), 100, 1, new(new(-12, -12), new(13, 13)),
            new("actor", "character", new(0, 0), 10, 10, "ai"), prototypes,
            new Dictionary<string, CollisionMask> { ["grass"] = Ground, ["water"] = Solid },
            Enumerable.Range(-12, 25).Select(y => new TileRun(-12, y, 25, "grass")).ToArray(),
            entities, new Dictionary<string, PlaceableItem>
            {
                ["furnace"] = new("furnace", 50), ["chest"] = new("chest", 50), ["pump"] = new("pump", 50)
            }, new(true, true, "current-character-local-area", 12));
    }
}
