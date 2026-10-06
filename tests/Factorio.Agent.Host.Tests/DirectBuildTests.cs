using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class DirectBuildTests
{
    [Fact]
    public void APieceWithinReachAndClearOfTheActorIsBuiltFromWhereItStands()
    {
        var map = SpatialPlannerTests.Map([]);
        Assert.True(PlacementPlanner.BuildableFromHere(map, "chest", new(new(5.5, .5), 0, 0)));
        Assert.False(PlacementPlanner.BuildableFromHere(map, "chest", new(new(9.5, .5), 0, 0)));
        Assert.False(PlacementPlanner.BuildableFromHere(map, "chest", new(new(.5, .5), 0, 0)));
    }

    [Fact]
    public void AnApproachForSequentialPiecesAlsoReachesTheNextOnes()
    {
        var field = new SpatialCollisionField(SpatialPlannerTests.Map([]));
        var placement = new PlacementCandidate(new(11.5, .5), 0, 0);
        MapPosition[] upcoming = [new(11.5, -3.5), new(11.5, -6.5), new(11.5, -9.5)];
        var nearest = new PlacementPlanner().FindApproach(field, "chest", placement)!;
        var covering = new PlacementPlanner().FindApproach(field, "chest", placement, upcoming: upcoming)!;
        double reach = field.Map.Actor.BuildDistance - 1;
        Assert.True(upcoming.Count(u => u.DistanceTo(covering) <= reach) > upcoming.Count(u => u.DistanceTo(nearest) <= reach));
        Assert.All(upcoming, u => Assert.True(u.DistanceTo(covering) <= reach));
        Assert.True(covering.DistanceTo(placement.Position) <= reach);
    }
}
