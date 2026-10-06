namespace Factorio.Agent.Core;

/// <summary>Conservative construction reservations from stationary threats seen by the controlled actor.</summary>
public static class TransportConstructionSafety
{
    // Reserve the navigation envelope itself, rather than placing machinery which requires entering it.
    // Native reach and collision checks still decide the actual approach at execution time.
    public static bool Allows(SpatialSnapshot map, MapPosition position) =>
        (map.StationaryThreats ?? []).All(t => position.DistanceTo(t.Position) >= t.Range + ExplorationPlanner.ThreatMargin);
}
