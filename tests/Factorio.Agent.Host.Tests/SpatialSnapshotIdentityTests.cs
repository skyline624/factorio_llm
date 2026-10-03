using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SpatialSnapshotIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlappingBodiesRequireDistinctNativeIdentities(bool duplicate)
    {
        var map = SpatialPlannerTests.Map([]);
        var body = new EntityGeometry("small-biter-corpse", "corpse", new(new(-.2, -.2), new(.2, .2)),
            new([], false, false, false), 1, 1);
        map = map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { [body.Name] = body },
            Entities = [new("body:10", body.Name, new(2, 0), body.CollisionBox.Translate(new(2, 0)), 0, "neutral"),
                new(duplicate ? "body:10" : "body:11", body.Name, new(2, 0), body.CollisionBox.Translate(new(2, 0)), 0, "neutral")]
        };
        var response = new GameResponse(1, "synthetic", true, map.CollectedTick, Protocol.ToElement(map));
        if (duplicate) Assert.Throws<InvalidDataException>(() => SpatialSnapshot.Parse(response));
        else Assert.Equal(2, SpatialSnapshot.Parse(response).Entities.Count);
    }
}
