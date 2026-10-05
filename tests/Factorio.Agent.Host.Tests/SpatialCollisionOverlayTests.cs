using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SpatialCollisionOverlayTests
{
    [Fact]
    public void PlannedObstaclesRemainInChildrenWithoutMutatingTheObservedField()
    {
        var map = BeltRoutePlannerTests.Map();
        var field = new SpatialCollisionField(map);
        var wall = map.Prototypes["wall"];
        MapPosition first = new(3.5,.5), second = new(5.5,.5);
        var child = field.AppendEntities([new("planned:first","wall",first,wall.CollisionBox.Translate(first),0,"own")]);
        var grandchild = child.AppendEntities([new("planned:second","wall",second,wall.CollisionBox.Translate(second),0,"own")]);
        var belt = map.Prototypes["belt"];
        Assert.True(field.PlacementClear(belt,first,0));
        Assert.True(field.PlacementClear(belt,second,0));
        Assert.False(child.PlacementClear(belt,first,0));
        Assert.True(child.PlacementClear(belt,second,0));
        Assert.False(grandchild.PlacementClear(belt,first,0));
        Assert.False(grandchild.PlacementClear(belt,second,0));
        Assert.Equal(map.Entities.Count,field.Map.Entities.Count);
        Assert.Equal(map.Entities.Count+2,grandchild.Map.Entities.Count);
    }

    [Fact]
    public void InheritedNativeTerrainAndTileBuildRulesAreStillApplied()
    {
        var map = BeltRoutePlannerTests.Map();
        map = map with
        {
            TilePrototypes = new Dictionary<string,CollisionMask>(map.TilePrototypes) { ["blocked"] = map.Prototypes["belt"].Mask },
            TileFluids = new Dictionary<string,string> { ["blocked"] = "water" },
            Rows = [..map.Rows.Where(r=>r.Y != 0), new(-12,0,12,"grass"),new(0,0,1,"blocked"),new(1,0,12,"grass")]
        };
        var field = new SpatialCollisionField(map);
        var wall = map.Prototypes["wall"];
        var child = field.AppendEntities([new("planned","wall",new(3.5,.5),wall.CollisionBox.Translate(new(3.5,.5)),0,"own")]);
        Assert.Equal("water",child.FluidAt(new(.5,.5)));
        Assert.False(child.PlacementClear(map.Prototypes["belt"],new(.5,.5),0));
        Assert.True(child.PlacementClear(map.Prototypes["belt"],new(1.5,.5),0));
    }

    [Fact]
    public void DuplicatePlannedIdentitiesCannotReplaceAnExistingObstacle()
    {
        var map = BeltRoutePlannerTests.Map();
        var wall = map.Prototypes["wall"];
        var entity = new SpatialEntity("planned","wall",new(3.5,.5),wall.CollisionBox.Translate(new(3.5,.5)),0,"own");
        var field = new SpatialCollisionField(map).AppendEntities([entity]);
        Assert.Throws<InvalidDataException>(()=>field.AppendEntities([entity with { Position = new(6.5,.5) }]));
        Assert.False(field.PlacementClear(map.Prototypes["belt"],new(3.5,.5),0));
    }
}
