using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class BeltTransportNetworkTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("branch")]
    [InlineData("refill")]
    public void NativeGraphMustContainOnlyTheRequestedDirectedChain(string mutation)
    {
        var map = BeltTransportPlannerTests.Map(true);
        map = map with { Entities = [.. map.Entities,
            new("a", "arm", new(1.5, .5), new(new(1.35, .35), new(1.65, .65)), 12, "own",
                DropTargetId: "b1", Power: new(10, 1), PickupTargetId: "source"),
            new("z", "arm", new(7.5, .5), new(new(7.35, .35), new(7.65, .65)), 12, "own",
                DropTargetId: "target", Power: new(10, 1), PickupTargetId: "b2"),
            new("b1", "belt", new(2.5, .5), new(new(2.1, .1), new(2.9, .9)), 4, "own",
                BeltConnections: new([], mutation == "branch" ? ["b2", "foreign"] : ["b2"])),
            new("b2", "belt", new(3.5, .5), new(new(3.1, .1), new(3.9, .9)), 4, "own",
                BeltConnections: new(["b1"], []))] };
        if (mutation == "refill") map = map with { Entities = [.. map.Entities,
            new("feeder", "arm", new(-.5, .5), new(new(-.65, .35), new(-.35, .65)), 4, "own", DropTargetId: "source")] };
        if (mutation != "none") Assert.Throws<InvalidDataException>(() => BeltTransportNetwork.Verify(map, "source", "target", "a", "z", ["b1", "b2"]));
        else Assert.Equal(new[] { "b1", "b2" }, BeltTransportNetwork.Find(map, "source", "target")!.BeltIds);
    }

    [Fact]
    public void OutputStorageLeavesRoomForACompleteTransportLayout()
    {
        var map = BeltTransportPlannerTests.Map(true);
        Assert.NotNull(new MachineOutputBufferPlanner().Find(map, "target", "chest", new("belt", "arm", "pole")));
    }
}
