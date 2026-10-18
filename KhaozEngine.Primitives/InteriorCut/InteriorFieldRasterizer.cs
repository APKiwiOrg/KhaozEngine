using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Primitives;

internal static class InteriorFieldRasterizer
{
    internal static bool Covers(IReadOnlyList<Vector2> outline, Vector2 min, Vector2 max)
    {
        if (!Contains(outline, min) || !Contains(outline, max) ||
            !Contains(outline, new Vector2(min.X, max.Y)) || !Contains(outline, new Vector2(max.X, min.Y)))
            return false;
        // Corners alone miss a concave notch that enters and leaves through the same cell edge.
        for (int i = 0; i < outline.Count; i++)
            if (CrossesInterior(outline[i], outline[(i + 1) % outline.Count], min, max)) return false;
        return true;
    }

    internal static void Validate(IReadOnlyList<Vector2> outline)
    {
        ArgumentNullException.ThrowIfNull(outline);
        if (outline.Count < 3) throw new ArgumentException("Interior ring needs at least three vertices.", nameof(outline));
        double area = 0d;
        for (int i = 0; i < outline.Count; i++)
        {
            Vector2 a = outline[i], b = outline[(i + 1) % outline.Count];
            if (!float.IsFinite(a.X) || !float.IsFinite(a.Y) || a == b)
                throw new ArgumentException("Interior ring needs finite, distinct adjacent vertices.", nameof(outline));
            area += (double)a.X * b.Y - (double)b.X * a.Y;
            Vector2 c = outline[(i + 2) % outline.Count];
            if (Cross(a, b, c) == 0d && (((double)b.X - a.X) * ((double)c.X - b.X) + ((double)b.Y - a.Y) * ((double)c.Y - b.Y)) <= 0d)
                throw new ArgumentException("Interior ring cannot double back along an edge.", nameof(outline));
            for (int j = i + 2; j < outline.Count; j++)
            {
                if (i == 0 && j == outline.Count - 1) continue;
                if (Intersects(a, b, outline[j], outline[(j + 1) % outline.Count]))
                    throw new ArgumentException("Interior ring must be simple and cannot touch itself.", nameof(outline));
            }
        }
        if (area == 0d) throw new ArgumentException("Interior ring must enclose an area.", nameof(outline));
    }

    static bool Contains(IReadOnlyList<Vector2> outline, Vector2 point)
    {
        bool inside = false;
        for (int i = 0; i < outline.Count; i++)
        {
            Vector2 a = outline[i], b = outline[(i + 1) % outline.Count];
            if (OnSegment(a, b, point)) return true;
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < ((double)b.X - a.X) * (point.Y - (double)a.Y) / (b.Y - (double)a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    static bool CrossesInterior(Vector2 a, Vector2 b, Vector2 min, Vector2 max)
    {
        double enter = 0d, exit = 1d;
        if (!ClipOpen(a.X, (double)b.X - a.X, min.X, max.X, ref enter, ref exit) ||
            !ClipOpen(a.Y, (double)b.Y - a.Y, min.Y, max.Y, ref enter, ref exit)) return false;
        return enter < exit;
    }

    static bool ClipOpen(double origin, double direction, double min, double max, ref double enter, ref double exit)
    {
        if (direction == 0d) return origin > min && origin < max;
        double a = (min - origin) / direction, b = (max - origin) / direction;
        enter = Math.Max(enter, Math.Min(a, b));
        exit = Math.Min(exit, Math.Max(a, b));
        return enter < exit;
    }

    static double Cross(Vector2 a, Vector2 b, Vector2 c) =>
        ((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.X - a.X);

    static bool OnSegment(Vector2 a, Vector2 b, Vector2 p) => Cross(a, b, p) == 0d &&
        p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X) && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);

    static bool Intersects(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        double abC = Cross(a, b, c), abD = Cross(a, b, d), cdA = Cross(c, d, a), cdB = Cross(c, d, b);
        return (abC > 0d && abD < 0d || abC < 0d && abD > 0d) &&
            (cdA > 0d && cdB < 0d || cdA < 0d && cdB > 0d) ||
            OnSegment(a, b, c) || OnSegment(a, b, d) || OnSegment(c, d, a) || OnSegment(c, d, b);
    }
}
