using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SurveyedTransportFrameTests
{
    [Fact]
    public void AStationaryEnvelopeKeepsItsActualSightingDateAfterTheActorWalksAway()
    {
        var atlas = new SurveyedTransportFrame();
        atlas.Add(Photo(0, 10) with { StationaryThreats = [new("seen-worm", new(.5, .5), 25, 10)] });
        atlas.Add(Photo(100, 20) with { StationaryThreats = [] });
        var map = atlas.Build();
        var threat = Assert.Single(map.StationaryThreats!);
        Assert.Equal(10, threat.CollectedTick);
        Assert.Equal(20, map.CollectedTick);
        Assert.Equal("historical-character-survey-with-blocked-unknown-tiles", map.Coverage.Visibility);
        Assert.False(TransportConstructionSafety.Allows(map, new(30.5, .5)));
        Assert.True(TransportConstructionSafety.Allows(map, new(60.5, .5)));
    }

    [Fact]
    public void UnsurveyedGapsCannotBecomeAFreeRouteBetweenDistantFactories()
    {
        var atlas = new SurveyedTransportFrame();
        atlas.Add(Photo(0, 10)); atlas.Add(Photo(100, 20));
        var map = atlas.Build();
        Assert.False(map.Coverage.Atomic);
        Assert.False(map.Coverage.Complete);
        Assert.Equal(10, atlas.FirstTick);
        Assert.Equal(20, map.CollectedTick);
        var geometry = map.Prototypes[map.Items["belt"].EntityName];
        Assert.False(new SpatialCollisionField(map).PlacementClear(geometry, new(50.5, .5), 4));
        Assert.Equal(BeltRouteStatus.NoRouteInSnapshot, new BeltRoutePlanner().Find(map, "belt", new(.5, .5), new(100.5, .5)).Status);
    }

    [Fact]
    public void NativePhotosAlongTheActorsTripPermitARouteBeyondTheOldFrame()
    {
        var atlas = new SurveyedTransportFrame();
        for (int x = 0; x <= 420; x += 10) atlas.Add(Photo(x, x + 10));
        var route = new BeltRoutePlanner().Find(atlas.Build(), "belt", new(.5, .5), new(420.5, .5));
        Assert.Equal(BeltRouteStatus.Found, route.Status);
        Assert.True(route.Belts.Count > 400);
        Assert.All(route.Belts, b => Assert.True(b.Position.Y is > -10 and < 10));
    }

    [Fact]
    public void AnOverlapRemovesAChangedEntityOnlyInsideTheNewObservation()
    {
        var initial = Photo(0, 10);
        var wall = initial.Prototypes["wall"];
        var kept = new SpatialEntity("kept", "wall", new(-8.5, .5), wall.CollisionBox.Translate(new(-8.5, .5)), 0, "own");
        var removed = kept with { Id = "removed", Position = new(.5, .5), Bounds = wall.CollisionBox.Translate(new(.5, .5)) };
        var atlas = new SurveyedTransportFrame();
        atlas.Add(initial with { Entities = [kept, removed] }); atlas.Add(Photo(10, 20));
        var map = atlas.Build();
        Assert.Contains(map.Entities, e => e.Id == "kept");
        Assert.DoesNotContain(map.Entities, e => e.Id == "removed");
    }

    [Theory]
    [InlineData("world")]
    [InlineData("incarnation")]
    [InlineData("surface")]
    [InlineData("time")]
    [InlineData("partial")]
    public void AChangedIdentityOrIncompletePhotoCannotJoinTheCorridor(string change)
    {
        var first = Photo(0, 10);
        var next = Photo(10, 20);
        next = change switch
        {
            "world" => next with { Scope = next.Scope with { WorldId = "other" } },
            "incarnation" => next with { Scope = next.Scope with { Incarnation = next.Scope.Incarnation + 1 } },
            "surface" => next with { SurfaceIndex = next.SurfaceIndex + 1 },
            "time" => next with { CollectedTick = 9 },
            _ => next with { Coverage = next.Coverage with { Complete = false } }
        };
        var atlas = new SurveyedTransportFrame(); atlas.Add(first);
        Assert.Throws<InvalidDataException>(() => atlas.Add(next));
        Assert.Equal(1, atlas.Samples);
    }

    [Fact]
    public void AGapCannotInflateTheSurveyBeyondItsBoundedTileBudget()
    {
        var atlas = new SurveyedTransportFrame(); atlas.Add(Photo(0, 10));
        Assert.Throws<InvalidOperationException>(() => atlas.Add(Photo(50000, 20)));
        Assert.Equal(1, atlas.Samples);
    }

    private static SpatialSnapshot Photo(int x, long tick)
    {
        var map = BeltTransportPlannerTests.Map(true);
        return map with
        {
            CollectedTick = tick, Bounds = new(new(x - 10, -10), new(x + 11, 11)),
            Actor = map.Actor with { Position = new(x + .5, .5) }, Entities = [],
            Rows = Enumerable.Range(-10, 21).Select(y => new TileRun(x - 10, y, 21, map.Rows[0].Name)).ToArray(),
            Coverage = new(true, true, "current-character-local-area", 10)
        };
    }
}
