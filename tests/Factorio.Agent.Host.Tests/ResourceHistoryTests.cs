using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ResourceHistoryTests
{
    [Fact]
    public void MatchedCompletedMiningRecoversOnlyTheNativeHistoricalPosition()
    {
        var (map, catalog, submission, receipt) = Evidence();
        var sighting = ResourceHistoryImporter.Correlate(submission, receipt, catalog, map);
        Assert.NotNull(sighting);
        Assert.Equal(new MapPosition(12.5, 20.5), sighting.Position);
        Assert.Equal(80, sighting.ObservedTick);
    }

    [Fact]
    public void WrongWorldSurfaceCorrelationOrFutureCompletionCannotSeedMemory()
    {
        var (map, catalog, submission, receipt) = Evidence();
        Assert.Null(ResourceHistoryImporter.Correlate(submission, receipt with { OperationId = "other" }, catalog, map));
        Assert.Null(ResourceHistoryImporter.Correlate(submission, receipt with { Status = "running" }, catalog, map));
        Assert.Null(ResourceHistoryImporter.Correlate(submission, receipt with { UpdatedTick = 101 }, catalog, map));
        Assert.Null(ResourceHistoryImporter.Correlate(submission, receipt, catalog, map with { SurfaceIndex = 2 }));
        Assert.Null(ResourceHistoryImporter.Correlate(submission, receipt, catalog, map with { Scope = map.Scope with { WorldId = "other" } }));
    }

    private static (SpatialSnapshot, ProductionCatalog, OperationSubmission, OperationReceipt) Evidence()
    {
        var map = ResourceMemoryTests.Map();
        var catalog = new ProductionCatalog(map.Scope, map.CollectedTick, [], new Dictionary<string, NativeItem>(),
            new Dictionary<string, NativeMaterial[]> { ["copper-ore"] = [new("copper-ore", "item", 1)] },
            new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
        var submission = OperationSubmission.Create(map.Scope with { SessionId = "old" }, "mine",
            new { name = "copper-ore", position = new MapPosition(12.5, 20.5), count = 1 }, 90);
        var receipt = new OperationReceipt(submission.OperationId, "mine", "completed", 70, 80,
            Protocol.ToElement(new { targetId = "1:copper-ore:12.5:20.5", product = "copper-ore", produced = 1 }), null, Protocol.ToElement(new { }));
        return (map, catalog, submission, receipt);
    }
}
