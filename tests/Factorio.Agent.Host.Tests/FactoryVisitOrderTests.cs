using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryVisitOrderTests
{
    [Fact]
    public void ScatteredStoredOutputsDoNotFollowCreationOrderBackAndForth()
    {
        var snapshot = Snapshot(0, ("east", 20), ("west", -20), ("east2", 21), ("west2", -21));
        var route = FactoryVisitOrder.Plan(snapshot, ["east", "west", "east2", "west2"]);
        Assert.Equal(["east", "east2", "west", "west2"], route);
        var positions = new Dictionary<string, MapPosition> { ["east"] = new(20, 0), ["west"] = new(-20, 0), ["east2"] = new(21, 0), ["west2"] = new(-21, 0) };
        Assert.Equal(63, FactoryVisitOrder.Distance(new(0, 0), route, positions));
        Assert.Equal(143, FactoryVisitOrder.Distance(new(0, 0), ["east", "west", "east2", "west2"], positions));
    }

    [Fact]
    public void ALongerNearestNeighbourTourKeepsTheShorterExistingOrder()
    {
        Assert.Equal(["left", "near", "right"], FactoryVisitOrder.Plan(Snapshot(0, ("left", -5), ("near", 4), ("right", 10)),
            ["left", "near", "right"]));
    }

    [Fact]
    public void ANewActorPositionChangesTheVisitOrder()
    {
        Assert.Equal(["right", "middle", "left"], FactoryVisitOrder.Plan(Snapshot(100, ("left", 0), ("middle", 50), ("right", 100)),
            ["left", "middle", "right"]));
    }

    [Fact]
    public void UnknownOrForeignStopsAreRetainedOnceWithoutInventedPositions()
    {
        var snapshot = Snapshot(0, ("a", 0), ("b", 0));
        snapshot = snapshot with { Records = [.. snapshot.Records, Entity("foreign", 10, "foreign")] };
        Assert.Equal(["a", "b", "missing", "foreign"], FactoryVisitOrder.Plan(snapshot, ["missing", "a", "a", "foreign", "b"]));
    }

    [Fact]
    public void EqualLengthToursKeepTheirExistingOrder()
    {
        Assert.Equal(["b", "a"], FactoryVisitOrder.Plan(Snapshot(0, ("a", 0), ("b", 0)), ["b", "a"]));
    }

    private static FactorySnapshot Snapshot(double start, params (string Id, double X)[] stops) => new("s",
        new("world", "session", "actor", 1, 1), 100, 200, Protocol.ToElement(new { atomic = true }),
        [Entity("actor", start, "actor"), .. stops.Select(s => Entity(s.Id, s.X, "factory"))]);

    private static FactoryRecord Entity(string id, double x, string role) => new("entity:" + id, "entity", id, "iron-chest",
        Protocol.ToElement(new { role, position = new MapPosition(x, 0) }));
}
