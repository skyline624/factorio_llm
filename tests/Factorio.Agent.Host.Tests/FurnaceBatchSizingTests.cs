using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FurnaceBatchSizingTests
{
    private static readonly NativeRecipe Steel = new("steel", true, "smelting", 16,
        [new("iron", "item", 5)], [new("steel", "item", 1)], false);

    [Fact]
    public void ALoadedFurnaceDoesNotPromiseBatchesBeyondLoadedAndCarriedIngredients()
    {
        Assert.Equal(5, FurnaceBatchSizing.SuppliedBatches(Steel, 25, 0));
        Assert.Equal(7, FurnaceBatchSizing.SuppliedBatches(Steel, 25, 13));
        Assert.Equal(0, FurnaceBatchSizing.SuppliedBatches(Steel, 4, 0));
    }

    [Fact]
    public void MissingCapacityEvidenceIsNotReplacedByAnArbitraryBatchSize()
    {
        Assert.Throws<InvalidDataException>(() => FurnaceBatchSizing.Limit(Steel, new Dictionary<string, NativeItem>(), 20));
    }

    [Fact]
    public void SlowFullStackFitsTheObservationWindowBeforeFuelAndCollectionOverhead()
    {
        int observations = FurnaceBatchSizing.ObservationLimit(Steel, 0.5, 20);
        Assert.True(observations > 640); // Twenty native 16-second cycles at half speed need 640 ordinary one-second waits.
    }
}
