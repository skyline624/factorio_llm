using System.Globalization;
using System.Text.Json.Serialization;

namespace Factorio.Agent.Core;

/// <summary>Own industry worth defending: an entity footprint (by native id) or a factory band (Entity false).</summary>
public sealed record IndustryMember(string Id, WorldBox Box, bool Entity = true);

/// <summary>Own industry one ring protects; its box leaves room for the ring margins inside one native observation.</summary>
public sealed record IndustryCluster(string Id, WorldBox Box, IReadOnlyList<string> Members)
{
    [JsonIgnore] public MapPosition Center => new((Box.Min.X + Box.Max.X) / 2, (Box.Min.Y + Box.Max.Y) / 2);
}

/// <summary>
/// Groups own industry into sites that each get one perimeter. On 2026-10-01 (seed 20261002) one ring around bands and
/// distant resource rows could not be observed at once ("The factory core alone exceeds one observed perimeter"), so the
/// factory stayed unprotected. Members closer than <see cref="Link"/> share a site while the site still fits one
/// observation; a longer chain is split.
/// </summary>
public static class IndustryClusterPlanner
{
    /// <summary>Tiles between two footprints below which one ring protects both.</summary>
    public const double Link = 20;

    /// <summary>
    /// Largest site extent: a 97-tile native observation minus the ring margins on both sides (3-tile opening, 2-tile turret,
    /// two wall layers, 2 proof tiles) and slack for the observation centre and approximate member footprints.
    /// </summary>
    public const double MaximumExtent = 75;

    public static IReadOnlyList<IndustryCluster> Group(IReadOnlyList<IndustryMember> members)
    {
        // Ordinal order and gap-sorted unions make the grouping independent of input order.
        var nodes = members.DistinctBy(m => m.Id).OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
        int[] parent = Enumerable.Range(0, nodes.Length).ToArray();
        var boxes = nodes.Select(n => n.Box).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        var pairs = new List<(double Gap, int A, int B)>();
        var byX = Enumerable.Range(0, nodes.Length).OrderBy(i => nodes[i].Box.Min.X).ToArray();
        for (int a = 0; a < byX.Length; a++)
            for (int b = a + 1; b < byX.Length && nodes[byX[b]].Box.Min.X - nodes[byX[a]].Box.Max.X <= Link; b++)
            {
                double gap = Gap(nodes[byX[a]].Box, nodes[byX[b]].Box);
                if (gap <= Link) pairs.Add((gap, Math.Min(byX[a], byX[b]), Math.Max(byX[a], byX[b])));
            }
        foreach (var (_, a, b) in pairs.OrderBy(p => p.Gap).ThenBy(p => p.A).ThenBy(p => p.B))
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) continue;
            var merged = Union(boxes[ra], boxes[rb]);
            if (merged.Width > MaximumExtent || merged.Height > MaximumExtent) continue;
            parent[rb] = ra;
            boxes[ra] = merged;
        }
        return Enumerable.Range(0, nodes.Length).GroupBy(Find).Select(g =>
        {
            var group = g.Select(i => nodes[i]).ToArray();
            return new IndustryCluster(Name(group), boxes[g.Key], group.Select(m => m.Id).Order(StringComparer.Ordinal).ToArray());
        }).OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Named after its oldest entity (lowest native id), so the name survives growth and carries no coordinate.</summary>
    private static string Name(IReadOnlyList<IndustryMember> group)
    {
        var entity = group.Where(m => m.Entity).Select(m => m.Id)
            .OrderBy(id => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out long n) ? n : long.MaxValue)
            .ThenBy(id => id, StringComparer.Ordinal).FirstOrDefault();
        return "cluster-" + (entity ?? group.Select(m => m.Id).Order(StringComparer.Ordinal).First());
    }

    /// <summary>Euclidean gap between two rectangles; zero when they touch or overlap.</summary>
    public static double Gap(WorldBox a, WorldBox b)
    {
        double dx = Math.Max(0, Math.Max(a.Min.X - b.Max.X, b.Min.X - a.Max.X));
        double dy = Math.Max(0, Math.Max(a.Min.Y - b.Max.Y, b.Min.Y - a.Max.Y));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Distance from a point to a rectangle; zero inside.</summary>
    public static double Distance(WorldBox box, MapPosition point) => Gap(box, new(point, point));

    private static WorldBox Union(WorldBox a, WorldBox b) =>
        new(new(Math.Min(a.Min.X, b.Min.X), Math.Min(a.Min.Y, b.Min.Y)), new(Math.Max(a.Max.X, b.Max.X), Math.Max(a.Max.Y, b.Max.Y)));
}
