namespace Factorio.Agent.Core;

/// <summary>Reads the death zones still active at an observed tick; the session client owns the durable file.</summary>
public interface IDangerZoneReader
{
    Task<IReadOnlyList<NativeDeathTransition>> ReadActiveDeathsAsync(ActorScope scope, int surfaceIndex, long tick,
        CancellationToken token = default);
}

/// <summary>
/// Recent own deaths, never enemy positions. Each death marks a zone that exploration avoids and corpse recovery does not
/// approach until it expires, unless a normal observation shows no mobile enemy inside it. Nothing here grants sight.
/// </summary>
public sealed record DangerZones(int Version, string WorldId, IReadOnlyList<NativeDeathTransition> Deaths)
{
    /// <summary>Base 2.0.77 biters see 30 tiles (<c>vision_distance</c>); one chunk also covers a pack standing on the corpse.</summary>
    public const double Radius = 32;
    /// <summary>
    /// Twenty game minutes. On 2026-10-01 (seed 20261002) deaths during corpse recovery came 33 to 58 s after the death they
    /// were recovering, and one death fell 30 tiles from an earlier one 17.3 minutes later.
    /// </summary>
    public const long LifetimeTicks = 20 * 60 * 60;
    public const int Capacity = 32;

    public static DangerZones Empty(string worldId) => new(1, worldId, []);
    public static long Expires(NativeDeathTransition death) => death.DeathTick + LifetimeTicks;
    public static bool ActiveAt(NativeDeathTransition death, long tick) => death.DeathTick <= tick && tick < Expires(death);
    public static bool Covers(NativeDeathTransition death, MapPosition point) => point.DistanceTo(death.Position) <= Radius;

    /// <summary>Whether the straight way between two points enters the zone; a planned route may still bend around it.</summary>
    public static bool Crosses(NativeDeathTransition death, MapPosition from, MapPosition to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y, length = dx * dx + dy * dy;
        double ratio = length == 0 ? 0 : Math.Clamp(((death.Position.X - from.X) * dx + (death.Position.Y - from.Y) * dy) / length, 0, 1);
        return Covers(death, new(from.X + dx * ratio, from.Y + dy * ratio));
    }

    /// <summary>
    /// Verdict of one normal observation on a zone: <c>clear</c>, <c>occupied</c> or <c>out-of-sight</c>. The character always sees
    /// the 5x5 chunks around its own chunk, which contain every point within 64 tiles, so a complete enemy list of that radius
    /// covers a zone whose whole disk it contains. Worms are left to routing, which already keeps out of their attack range.
    /// </summary>
    public static string Inspect(NativeDeathTransition death, MapPosition observer, double observedRadius, bool complete,
        IEnumerable<(string Type, MapPosition Position)> enemies)
    {
        if (!complete || observedRadius > 64 || observer.DistanceTo(death.Position) + Radius > observedRadius) return "out-of-sight";
        return enemies.Any(e => e.Type is "unit" or "unit-spawner" && Covers(death, e.Position)) ? "occupied" : "clear";
    }

    /// <summary>Adds a death once, forgets zones expired by the newest death and keeps the newest <see cref="Capacity"/>.</summary>
    public DangerZones Record(NativeDeathTransition death)
    {
        Validate(death);
        var same = Deaths.FirstOrDefault(d => d.Incarnation == death.Incarnation && d.DeathTick == death.DeathTick);
        if (same is not null) return same == death ? this : throw new InvalidDataException("Two native deaths share one identity.");
        long newest = Math.Max(death.DeathTick, Deaths.Select(d => d.DeathTick).DefaultIfEmpty(0).Max());
        return this with
        {
            Deaths = [.. Deaths.Append(death).Where(d => Expires(d) > newest)
                .OrderByDescending(d => d.DeathTick).Take(Capacity).OrderBy(d => d.DeathTick)]
        };
    }

    public IReadOnlyList<NativeDeathTransition> Active(int surfaceIndex, long tick) =>
        Deaths.Where(d => d.SurfaceIndex == surfaceIndex && ActiveAt(d, tick)).ToArray();

    public void Validate(string worldId)
    {
        if (Version != 1 || WorldId != worldId) throw new InvalidDataException("The danger zones belong to another world.");
        if (Deaths is null || Deaths.Count > Capacity) throw new InvalidDataException("Invalid danger zone count.");
        foreach (var death in Deaths) Validate(death);
    }

    private static void Validate(NativeDeathTransition death)
    {
        if (death is null || death.Incarnation < 1 || death.DeathTick < 0 || death.ActorUnitNumber < 1 || death.SurfaceIndex < 1
            || death.Position is null || !double.IsFinite(death.Position.X) || !double.IsFinite(death.Position.Y))
            throw new InvalidDataException("Invalid native death for a danger zone.");
    }
}
