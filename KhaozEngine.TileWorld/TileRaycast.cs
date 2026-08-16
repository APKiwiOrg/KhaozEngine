using System;
using System.Numerics;

namespace KhaozEngine.TileWorld;

/// <summary>A ray hit on the ground lattice.</summary>
public readonly record struct TileHit(int X, int Z, int Plane, Vector3 Point, float Distance);

/// <summary>Ray against the tile lattice, GPU-free, so the editor's click and the game's click-to-walk share
/// it. World units are tiles times <see cref="TileWorldDocument.TileSize"/> on x/z and metres on y, with world z
/// running opposite to tile z through <see cref="TileWorldSpace"/>.</summary>
public static class TileRaycast
{
    /// <summary>The first ground hit along the ray on this plane, or null when it crosses no solid tile.
    /// <paramref name="direction"/> need not be normalised, and the reported distance is in world units.</summary>
    public static TileHit? Pick(TileWorldDocument doc, int plane, Vector3 origin, Vector3 direction, float maxDistance = 2000f)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (direction.LengthSquared() < 1e-12f) return null;
        Vector3 dir = Vector3.Normalize(direction);
        float ts = doc.TileSize;

        // 2D DDA over tiles in XZ, run entirely in TILE space. The world-to-tile map is linear (a scale plus a
        // flip of z, no translation), so a DIRECTION converts exactly as a position does, and the flip is what
        // makes a ray with a positive world dir.Z walk toward DECREASING tile z. Everything below reads the
        // signs off dx and dz, so nothing else in the march has to know about the flip.
        float px = TileWorldSpace.TileX(origin.X, ts), pz = TileWorldSpace.TileZ(origin.Z, ts);
        int tx = (int)MathF.Floor(px), tz = (int)MathF.Floor(pz);
        float dx = TileWorldSpace.TileX(dir.X, ts), dz = TileWorldSpace.TileZ(dir.Z, ts);
        bool vertical = MathF.Abs(dx) < 1e-6f && MathF.Abs(dz) < 1e-6f;
        int stepX = dx > 0 ? 1 : -1, stepZ = dz > 0 ? 1 : -1;
        float tDeltaX = MathF.Abs(dx) < 1e-9f ? float.PositiveInfinity : MathF.Abs(1f / dx);
        float tDeltaZ = MathF.Abs(dz) < 1e-9f ? float.PositiveInfinity : MathF.Abs(1f / dz);
        float tMaxX = MathF.Abs(dx) < 1e-9f ? float.PositiveInfinity : (dx > 0 ? (tx + 1 - px) : (px - tx)) * tDeltaX;
        float tMaxZ = MathF.Abs(dz) < 1e-9f ? float.PositiveInfinity : (dz > 0 ? (tz + 1 - pz) : (pz - tz)) * tDeltaZ;

        float travelled = 0f;
        int guard = 0;
        while (travelled <= maxDistance && guard++ < 100_000)
        {
            if (TestTile(doc, plane, tx, tz, origin, dir, maxDistance, out TileHit hit)) return hit;
            if (vertical) return null;
            if (tMaxX < tMaxZ) { tx += stepX; travelled = tMaxX; tMaxX += tDeltaX; }
            else { tz += stepZ; travelled = tMaxZ; tMaxZ += tDeltaZ; }
        }
        return null;
    }

    static bool TestTile(TileWorldDocument doc, int plane, int tx, int tz, Vector3 origin, Vector3 dir, float maxDistance, out TileHit hit)
    {
        hit = default;
        if (doc.GetUnderlay(tx, tz, plane) == 0) return false;
        float ts = doc.TileSize;
        short h00 = doc.CornerHeightCm(tx, tz, plane), h10 = doc.CornerHeightCm(tx + 1, tz, plane);
        short h01 = doc.CornerHeightCm(tx, tz + 1, plane), h11 = doc.CornerHeightCm(tx + 1, tz + 1, plane);
        Vector3 sw = TileWorldSpace.ToWorld(tx, h00 * 0.01f, tz, ts);
        Vector3 se = TileWorldSpace.ToWorld(tx + 1, h10 * 0.01f, tz, ts);
        Vector3 nw = TileWorldSpace.ToWorld(tx, h01 * 0.01f, tz + 1, ts);
        Vector3 ne = TileWorldSpace.ToWorld(tx + 1, h11 * 0.01f, tz + 1, ts);
        TileOverlayShape authored = doc.GetOverlayShape(tx, tz, plane);
        int rotation = doc.GetOverlayRotation(tx, tz, plane);
        bool swne = TileTriangulation.SplitSwNe(h00, h10, h01, h11, authored, rotation);

        // The shape only cuts the tile when there is an overlay material to paint into the cut, which is exactly
        // what the mesher draws. Going through the shared triangulation is what keeps a click on the triangle
        // that is drawn: a corner cut is a four triangle fan, and testing the plain pair instead would report the
        // wrong height in the middle of a tile whose corners are not coplanar.
        TileOverlayShape shape = doc.GetOverlay(tx, tz, plane) != 0 ? authored : TileOverlayShape.Full;
        Span<TileTriangle> triangles = stackalloc TileTriangle[TileTriangulation.MaxTriangles];
        int count = TileTriangulation.Triangulate(shape, rotation, swne, triangles);

        // These triangles wind a DOWNWARD normal, harmless here because Intersect is two-sided.
        float best = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            Vector3 a = PointAt(triangles[i].A, sw, se, nw, ne);
            Vector3 b = PointAt(triangles[i].B, sw, se, nw, ne);
            Vector3 c = PointAt(triangles[i].C, sw, se, nw, ne);
            if (Intersect(origin, dir, a, b, c, out float t) && t < best) best = t;
        }
        if (float.IsPositiveInfinity(best) || best > maxDistance) return false;
        hit = new TileHit(tx, tz, plane, origin + dir * best, best);
        return true;
    }

    // Where a lattice point sits on this tile: a corner as it stands, a mid-edge point midway between the two
    // corners it lies between, which is the same averaging the mesher's vertices use.
    static Vector3 PointAt(TilePoint point, in Vector3 sw, in Vector3 se, in Vector3 nw, in Vector3 ne)
    {
        TileTriangulation.Ends(point, out TilePoint first, out TilePoint second);
        return (CornerAt(first, sw, se, nw, ne) + CornerAt(second, sw, se, nw, ne)) * 0.5f;
    }

    static Vector3 CornerAt(TilePoint corner, in Vector3 sw, in Vector3 se, in Vector3 nw, in Vector3 ne) => corner switch
    {
        TilePoint.Se => se,
        TilePoint.Nw => nw,
        TilePoint.Ne => ne,
        _ => sw,
    };

    /// <summary>Möller-Trumbore, both faces, t >= 0.</summary>
    static bool Intersect(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0f;
        Vector3 e1 = b - a, e2 = c - a;
        Vector3 p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f) return false;
        float inv = 1f / det;
        Vector3 s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < -1e-5f || u > 1f + 1e-5f) return false;
        Vector3 q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < -1e-5f || u + v > 1f + 1e-5f) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t >= 0f;
    }
}
