using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.TileWorld;

/// <summary>Exposed edges of the overlay union near one tile, queried from the global document.</summary>
internal sealed class TileOverlayBoundary
{
    readonly List<(Vector2 A, Vector2 B)> _edges = new();
    readonly TileWorldDocument _doc;
    readonly int _plane;
    readonly ushort _material;
    readonly int _x;
    readonly int _z;

    internal TileOverlayBoundary(TileWorldDocument doc, int x, int z, int plane)
    {
        _doc = doc;
        _plane = plane;
        _material = doc.GetOverlay(x, z, plane);
        _x = x;
        _z = z;
        Span<TileLatticeTriangle> triangles = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];
        // Width is at most half a tile. One neighbour ring contains every edge that can affect this tile.
        for (int nz = z - 1; nz <= z + 1; nz++)
            for (int nx = x - 1; nx <= x + 1; nx++)
            {
                if (!Drawable(nx, nz) || doc.GetOverlay(nx, nz, plane) != _material) continue;
                int count = Triangles(nx, nz, triangles);
                Vector2 origin = new(nx - x, nz - z);
                for (int i = 0; i < count; i++)
                {
                    TileLatticeTriangle t = triangles[i];
                    if (!t.Overlay) continue;
                    Vector2 a = origin + Point(t.A), b = origin + Point(t.B), c = origin + Point(t.C);
                    AddEdge(a, b, c);
                    AddEdge(b, c, a);
                    AddEdge(c, a, b);
                }
            }
    }

    internal float Distance(Vector2 regionPoint, int originX, int originZ)
    {
        // Integer subtraction before float conversion keeps narrow widths stable in distant regions.
        Vector2 point = regionPoint + new Vector2(originX - _x, originZ - _z);
        float best = float.PositiveInfinity;
        foreach ((Vector2 a, Vector2 b) in _edges)
        {
            Vector2 ab = b - a;
            float along = Math.Clamp(Vector2.Dot(point - a, ab) / ab.LengthSquared(), 0f, 1f);
            best = MathF.Min(best, Vector2.Distance(point, a + along * ab));
        }
        return best;
    }

    void AddEdge(Vector2 a, Vector2 b, Vector2 inside)
    {
        // Half-edge probes distinguish an adjacent quarter cut from a fully covered tile edge.
        Vector2 midpoint = (a + b) * 0.5f;
        AddHalf(a, midpoint, inside);
        AddHalf(midpoint, b, inside);
    }

    void AddHalf(Vector2 a, Vector2 b, Vector2 inside)
    {
        Vector2 edge = b - a;
        Vector2 outward = new(-edge.Y, edge.X);
        if (Vector2.Dot(outward, inside - a) > 0) outward = -outward;
        Vector2 probe = (a + b) * 0.5f + Vector2.Normalize(outward) * 0.001f;
        if (!Covered(probe)) _edges.Add((a, b));
    }

    bool Covered(Vector2 point)
    {
        int dx = (int)MathF.Floor(point.X), dz = (int)MathF.Floor(point.Y);
        int x = _x + dx, z = _z + dz;
        if (!Drawable(x, z)) return false;
        ushort overlay = _doc.GetOverlay(x, z, _plane);
        if (overlay == 0) return _doc.GetUnderlay(x, z, _plane) == _material;
        if (overlay != _material) return false;
        Vector2 local = point - new Vector2(dx, dz);
        Span<TileLatticeTriangle> triangles = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];
        int count = Triangles(x, z, triangles);
        for (int i = 0; i < count; i++)
        {
            TileLatticeTriangle t = triangles[i];
            if (t.Overlay && Contains(local, Point(t.A), Point(t.B), Point(t.C))) return true;
        }
        return false;
    }

    int Triangles(int x, int z, Span<TileLatticeTriangle> triangles)
    {
        TileOverlayShape shape = _doc.GetOverlayShape(x, z, _plane);
        int rotation = _doc.GetOverlayRotation(x, z, _plane);
        // Heights select a full tile's internal diagonal, which cannot change its overlay coverage.
        bool split = TileTriangulation.SplitSwNe(0, 0, 0, 0, shape, rotation);
        return TileTriangulation.Triangulate(shape, rotation, split, triangles);
    }

    bool Drawable(int x, int z) => TileGroundTriangles.IsDrawable(_doc, x, z, _plane);

    static bool Contains(Vector2 p, Vector2 a, Vector2 b, Vector2 c) =>
        Cross(b - a, p - a) >= -0.00001f && Cross(c - b, p - b) >= -0.00001f
        && Cross(a - c, p - c) >= -0.00001f;

    static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    static Vector2 Point(TileLatticePoint point) => point switch
    {
        TileLatticePoint.Sw => Vector2.Zero,
        TileLatticePoint.Se => Vector2.UnitX,
        TileLatticePoint.Nw => Vector2.UnitY,
        TileLatticePoint.Ne => Vector2.One,
        TileLatticePoint.MidS => new(0.5f, 0),
        TileLatticePoint.MidE => new(1, 0.5f),
        TileLatticePoint.MidN => new(0.5f, 1),
        TileLatticePoint.MidW => new(0, 0.5f),
        _ => throw new ArgumentOutOfRangeException(nameof(point)),
    };
}
