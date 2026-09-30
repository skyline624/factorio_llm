using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class PlacementRefusalTests
{
    private static OperationReceipt Build(string status, string? code = null, object? effects = null) =>
        new("op", "build", status, 1, 2, Protocol.ToElement(effects ?? new { }), code is null ? null : new(code, "native"), Protocol.ToElement(new { }));

    [Fact]
    public void OnlyNativePlacementRefusalsAreRefusalsNotShortagesOrOtherFailures()
    {
        Assert.Equal("42", PoweredMachineController.BuiltEntity(Build("completed", effects: new { entityId = "42" })));
        foreach (var code in new[] { "placement_blocked", "out_of_reach" })
            Assert.Throws<PlacementRefusedException>(() => PoweredMachineController.BuiltEntity(Build("failed", code)));
        // A missing item, a rejected submission or an interrupted build is not the engine refusing this tile.
        foreach (var receipt in new[] { Build("failed", "missing_item"), Build("rejected", "actor_busy"), Build("cancelled"), Build("failed", "actor_dead") })
        {
            var error = Assert.ThrowsAny<InvalidOperationException>(() => PoweredMachineController.BuiltEntity(receipt));
            Assert.IsNotType<PlacementRefusedException>(error);
        }
        // Lease loss and manual control must never be mistaken for a refusal by a catch of refusals.
        Assert.False(typeof(PlacementRefusedException).IsAssignableFrom(typeof(ActorControlUnavailableException)));
    }
}
