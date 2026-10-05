using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ResourceSearchProgressTests
{
    private static readonly ResourceSighting Coal = new("charted-coal", "coal", new(385.5, 20.5), 100, ResourceSighting.Charted);

    [Fact]
    public void ADistantChartedDepositCanBeReachedBeyondFourLocalWalks()
    {
        var budget = new ResourceSearchProgress(4);
        for (int step = 0; step < 14; step++)
        {
            Assert.False(budget.Exhausted);
            var before = new MapPosition(step * 28, 20.5);
            var arrived = new MapPosition(Math.Min(Coal.Position.X, (step + 1) * 28), 20.5);
            budget.BeginStep(new(arrived, 100 + step, Coal, before), before);
            budget.ObserveArrival(arrived, 101 + step, new HashSet<string>());
        }
        Assert.Equal(14, budget.ApproachSteps);
        Assert.Equal(0, budget.ExploratorySteps);
        Assert.False(budget.Exhausted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(1)]
    public void StalledOrRecedingTravelUsesTheOriginalBlindBudget(double progress)
    {
        var budget = new ResourceSearchProgress(4);
        for (int step = 0; step < 4; step++)
        {
            budget.BeginStep(new(new(28, 20.5), 100, Coal), new(0, 20.5));
            budget.ObserveArrival(new(progress, 20.5), 101, new HashSet<string>());
        }
        Assert.True(budget.Exhausted);
        Assert.Equal((4, 0), (budget.ExploratorySteps, budget.ApproachSteps));
    }

    [Fact]
    public void ADeferredUnusableDepositCannotExtendItsOwnSearch()
    {
        var budget = new ResourceSearchProgress(1);
        budget.BeginStep(new(new(28, 20.5), 100, Coal), new(0, 20.5));
        budget.ObserveArrival(new(28, 20.5), 101, new HashSet<string> { Coal.EntityId });
        Assert.True(budget.Exhausted);
        Assert.Equal((1, 0), (budget.ExploratorySteps, budget.ApproachSteps));
    }

    [Fact]
    public void ABlindFrontierRetainsTheOriginalBudgetEvenWhenItMovesFar()
    {
        var budget = new ResourceSearchProgress(1);
        budget.BeginStep(new(new(28, 0), 100), new(0, 0));
        budget.ObserveArrival(new(28, 0), 101, new HashSet<string>());
        Assert.True(budget.Exhausted);
        Assert.Equal((1, 0), (budget.ExploratorySteps, budget.ApproachSteps));
    }

    [Fact]
    public void ApproachingADepositStillHasAHardTravelBound()
    {
        var budget = new ResourceSearchProgress(4);
        var distant = Coal with { Position = new(2000, 0) };
        for (int step = 0; step < ResourceSearchProgress.MaximumApproachSteps; step++)
        {
            Assert.False(budget.Exhausted);
            var before = new MapPosition(step * 28, 0);
            var arrived = new MapPosition((step + 1) * 28, 0);
            budget.BeginStep(new(arrived, 100 + step, distant), before);
            budget.ObserveArrival(arrived, 101 + step, new HashSet<string>());
        }
        Assert.True(budget.Exhausted);
        Assert.Equal(ResourceSearchProgress.MaximumApproachSteps, budget.ApproachSteps);
    }

    [Fact]
    public void AStaleArrivalCannotProveProgressOrStartAnotherMutation()
    {
        var budget = new ResourceSearchProgress(4);
        budget.BeginStep(new(new(28, 20.5), 100, Coal), new(0, 20.5));
        Assert.Throws<InvalidDataException>(() => budget.ObserveArrival(new(28, 20.5), 99, new HashSet<string>()));
        Assert.Throws<InvalidOperationException>(() => budget.BeginStep(new(new(56, 20.5), 101, Coal), new(28, 20.5)));
        Assert.Equal((0, 0), (budget.ExploratorySteps, budget.ApproachSteps));
    }

    [Fact]
    public void ProgressUsesTheNativeWaypointOriginAndCannotBeCountedTwice()
    {
        var budget = new ResourceSearchProgress(4);
        // A kit may have collected supplies after the row planner's earlier observation.
        budget.BeginStep(new(new(28, 20.5), 100, Coal, new(56, 20.5)), new(0, 20.5));
        budget.ObserveArrival(new(28, 20.5), 101, new HashSet<string>());
        budget.ObserveArrival(new(100, 20.5), 102, new HashSet<string>());
        Assert.Equal((1, 0), (budget.ExploratorySteps, budget.ApproachSteps));
    }
}
