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

    [Fact]
    public void ConfirmedMissingConstructionItemPreservesItsNativeReceiptForMaintenance()
    {
        var receipt = Build("failed", "missing_item", new { inventoryDelta = new Dictionary<string, long>() });
        var error = Assert.Throws<ConstructionItemUnavailableException>(() => PoweredMachineController.BuiltEntity(receipt, "gun-turret"));
        Assert.Equal("gun-turret", error.Item);
        Assert.Same(receipt, error.Receipt);
        Assert.IsNotType<PlacementRefusedException>(error);
    }

    [Theory]
    [InlineData("running", "missing_item", "build")]
    [InlineData("partial", "missing_item", "build")]
    [InlineData("cancelled", "missing_item", "build")]
    [InlineData("rejected", "missing_item", "build")]
    [InlineData("failed", "actor_dead", "build")]
    [InlineData("failed", "missing_item", "insert")]
    public void OtherOutcomesCannotBecomeAMaintenanceShortfall(string status, string code, string kind)
    {
        var receipt = Build(status, code, new { inventoryDelta = new Dictionary<string, long>() }) with { Kind = kind };
        var error = Assert.ThrowsAny<InvalidOperationException>(() => PoweredMachineController.BuiltEntity(receipt, "gun-turret"));
        Assert.IsNotType<ConstructionItemUnavailableException>(error);
    }

    [Theory]
    [InlineData("unobserved")]
    [InlineData("inventory-changed")]
    [InlineData("entity-created")]
    public void UnprovenOrPartialBuildEffectsRemainFatal(string mutation)
    {
        object effects = mutation switch
        {
            "unobserved" => new { },
            "inventory-changed" => new { inventoryDelta = new Dictionary<string, long> { ["gun-turret"] = -1 } },
            _ => new { inventoryDelta = new Dictionary<string, long>(), entityId = "created" }
        };
        var error = Assert.ThrowsAny<InvalidOperationException>(() => PoweredMachineController.BuiltEntity(Build("failed", "missing_item", effects), "gun-turret"));
        Assert.IsNotType<ConstructionItemUnavailableException>(error);
    }

    [Fact]
    public void CompletedBuildStillRequiresItsNativeEntityIdentifier()
    {
        Assert.Equal("42", PoweredMachineController.BuiltEntity(Build("completed", effects: new { entityId = "42" }), "gun-turret"));
    }
}
