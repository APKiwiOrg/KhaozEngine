using System;
using System.Numerics;

namespace KhaozEngine.WorldMap.Raster;

/// <summary>Classifies polygon fill and unioned ink in scaled local metres without distorting stroke widths.</summary>
internal sealed class SymbolGeometrySampler
{
    readonly Vector2[] _outline;
    readonly WorldMapSymbolStroke[] _strokes;
    readonly Vector2 _halfExtents;

    internal SymbolGeometrySampler(WorldMapSymbolGeometry geometry, Vector2 halfExtents)
    {
        _halfExtents = halfExtents;
        _outline = new Vector2[geometry.Outline.Count];
        for (int i = 0; i < _outline.Length; i++) _outline[i] = geometry.Outline[i] * halfExtents;
        _strokes = new WorldMapSymbolStroke[geometry.Strokes.Count];
        for (int i = 0; i < _strokes.Length; i++)
            _strokes[i] = new(geometry.Strokes[i].Start * halfExtents, geometry.Strokes[i].End * halfExtents);
    }

    internal (bool Full, bool Fill, bool FillUnderDetail) Sample(Vector2 point, float width)
    {
        // Clip detail caps and joins to the footprint box so existing collection reach stays authoritative.
        if (_halfExtents.X <= 0f || _halfExtents.Y <= 0f ||
            MathF.Abs(point.X) > _halfExtents.X || MathF.Abs(point.Y) > _halfExtents.Y) return (false, false, false);
        bool inside = false;
        double boundaryDistance = double.PositiveInfinity;
        for (int i = 0; i < _outline.Length; i++)
        {
            Vector2 a = _outline[i], b = _outline[(i + 1) % _outline.Length];
            boundaryDistance = Math.Min(boundaryDistance, DistanceSquared(point, a, b));
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < a.X + ((double)point.Y - a.Y) * ((double)b.X - a.X) / ((double)b.Y - a.Y))
                inside = !inside;
        }
        inside |= boundaryDistance == 0d;
        bool boundary = inside && width > 0f && boundaryDistance <= (double)width * width;
        bool ink = boundary;
        if (!ink && width > 0f)
            foreach (WorldMapSymbolStroke stroke in _strokes)
                if (DistanceSquared(point, stroke.Start, stroke.End) <= (double)width * width * 0.25d)
                {
                    ink = true;
                    break;
                }
        return (inside || ink, inside && !ink, inside && ink && !boundary);
    }

    static double DistanceSquared(Vector2 point, Vector2 a, Vector2 b)
    {
        double dx = (double)b.X - a.X, dy = (double)b.Y - a.Y;
        double px = (double)point.X - a.X, py = (double)point.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        double t = lengthSquared == 0d ? 0d : Math.Clamp((px * dx + py * dy) / lengthSquared, 0d, 1d);
        double x = px - t * dx, y = py - t * dy;
        return x * x + y * y;
    }
}
