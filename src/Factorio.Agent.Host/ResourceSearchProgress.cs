using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Bounds blind searching separately from measured travel toward an observed historical deposit.</summary>
internal sealed class ResourceSearchProgress(int explorationBudget)
{
    internal const int MaximumApproachSteps = 32;
    private (ExplorationWaypoint Waypoint, MapPosition Before)? pending;
    public int ExploratorySteps { get; private set; }
    public int ApproachSteps { get; private set; }
    public bool Exhausted => ExploratorySteps >= explorationBudget || ApproachSteps >= MaximumApproachSteps;

    public void BeginStep(ExplorationWaypoint waypoint, MapPosition before)
    {
        if (pending is not null) throw new InvalidOperationException("Observe the previous resource search arrival before another step.");
        pending = (waypoint, waypoint.Origin ?? before);
    }

    public void ObserveArrival(MapPosition position, long tick, IReadOnlySet<string> deferredResources)
    {
        if (pending is not { } step) return;
        if (tick < step.Waypoint.CollectedTick) throw new InvalidDataException("Resource travel progress requires a fresh arrival observation.");
        if (step.Waypoint.RememberedResource is { } resource && !deferredResources.Contains(resource.EntityId)
            && step.Before.DistanceTo(resource.Position) - position.DistanceTo(resource.Position) > 1)
            ApproachSteps++;
        else ExploratorySteps++;
        pending = null;
    }
}
