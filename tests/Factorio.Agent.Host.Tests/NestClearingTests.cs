using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class NestClearingTests
{
    private static readonly IndustryCluster[] Industry = [new("base", new(new(0, 0), new(10, 10)), ["1"])];

    private static NestMemory Memory() => new(1, "world", [
        new("near-spawner", "biter-spawner", new(100, 0), null, 1), new("near-worm", "small-worm-turret", new(108, 4), 25, 1),
        new("far-spawner", "biter-spawner", new(500, 0), null, 1)]);

    [Fact]
    public void TheRememberedBaseClosestToIndustryIsChosenWithinReach()
    {
        var chosen = NestClearingController.Choose(Memory(), Industry, new(1, "world", []), 1000);
        Assert.NotNull(chosen);
        Assert.Equal(["near-spawner", "near-worm"], chosen.Structures.Select(s => s.Id));
        Assert.Null(NestClearingController.Choose(Memory(), [], new(1, "world", []), 1000));
    }

    [Fact]
    public void AFailedBaseWaitsForItsRetryDelayWhileAClearedOneDoesNotBlock()
    {
        var center = Memory().Clusters().Single(c => c.Structures.Count == 2).Center;
        var failed = new NestClearingState(1, "world", [new(center, 1000, "turret-losses")]);
        Assert.Null(NestClearingController.Choose(Memory(), Industry, failed, 1000 + NestClearingController.RetryTicks - 1));
        Assert.NotNull(NestClearingController.Choose(Memory(), Industry, failed, 1000 + NestClearingController.RetryTicks));
        var cleared = new NestClearingState(1, "world", [new(center, 1000, "cleared")]);
        Assert.NotNull(NestClearingController.Choose(Memory(), Industry, cleared, 1001));
    }

    [Fact]
    public void StagingStandsTowardTheActorBeyondWormRangeAndTheTravelMargin()
    {
        var nest = Memory().Clusters().Single(c => c.Structures.Count == 2);
        var staging = NestClearingController.Staging(nest, new(0, 0));
        Assert.True(staging.X < nest.Center.X);
        Assert.All(nest.Structures.Where(s => s.Range is not null),
            worm => Assert.True(staging.DistanceTo(worm.Position) > worm.Range!.Value + ExplorationPlanner.ThreatMargin));
    }
}
