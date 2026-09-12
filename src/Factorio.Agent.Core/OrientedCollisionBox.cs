namespace Factorio.Agent.Core;

/// <summary>Native oriented rectangle, intersected with the swept axis-aligned body using separating axes.</summary>
public sealed class OrientedCollisionBox
{
    private readonly MapPosition center;
    private readonly double halfWidth, halfHeight;
    private readonly (double X, double Y) horizontal, vertical;
    private readonly (double X, double Y)[] axes;
    public WorldBox EnclosingBox { get; }

    public OrientedCollisionBox(WorldBox bounds, double orientation = 0)
    {
        if (!double.IsFinite(orientation) || orientation is < 0 or >= 1)
            throw new InvalidDataException("Invalid native bounding box orientation.");
        center = new((bounds.Min.X + bounds.Max.X) / 2, (bounds.Min.Y + bounds.Max.Y) / 2);
        halfWidth = bounds.Width / 2;
        halfHeight = bounds.Height / 2;
        double angle = orientation * Math.Tau, cosine = Math.Cos(angle), sine = Math.Sin(angle);
        horizontal = (cosine, sine);
        vertical = (-sine, cosine);
        axes = orientation == 0 ? [(1, 0), (0, 1)] : [(1, 0), (0, 1), horizontal, vertical];
        double x = Math.Abs(cosine) * halfWidth + Math.Abs(sine) * halfHeight;
        double y = Math.Abs(sine) * halfWidth + Math.Abs(cosine) * halfHeight;
        EnclosingBox = new(new(center.X - x, center.Y - y), new(center.X + x, center.Y + y));
    }

    public bool IntersectsSweep(MapPosition from, MapPosition to, WorldBox body, double clearance = 0)
    {
        double bodyX = (body.Min.X + body.Max.X) / 2, bodyY = (body.Min.Y + body.Max.Y) / 2;
        double bodyWidth = body.Width / 2 + clearance, bodyHeight = body.Height / 2 + clearance;
        double low = 0, high = 1;
        // Axes of both rectangles are necessary: an enclosing AABB alone rejects legal cliff corners.
        foreach (var axis in axes)
        {
            double radius = halfWidth * Math.Abs(axis.X * horizontal.X + axis.Y * horizontal.Y)
                + halfHeight * Math.Abs(axis.X * vertical.X + axis.Y * vertical.Y)
                + bodyWidth * Math.Abs(axis.X) + bodyHeight * Math.Abs(axis.Y) - 1e-7;
            double start = (from.X + bodyX - center.X) * axis.X + (from.Y + bodyY - center.Y) * axis.Y;
            double speed = (to.X - from.X) * axis.X + (to.Y - from.Y) * axis.Y;
            if (Math.Abs(speed) < 1e-12)
            {
                if (Math.Abs(start) >= radius) return false;
                continue;
            }
            double enter = (-radius - start) / speed, leave = (radius - start) / speed;
            if (enter > leave) (enter, leave) = (leave, enter);
            low = Math.Max(low, enter);
            high = Math.Min(high, leave);
            if (low > high) return false;
        }
        return true;
    }

    public bool Overlaps(WorldBox body) => IntersectsSweep(new(0, 0), new(0, 0), body);
}
