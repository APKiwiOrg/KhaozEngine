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
        => PickCore(doc, plane, origin, direction, maxDistance, includeTile: null);

    // Render-facing seam for a view that has to skip terrain it did not draw. Kept internal so the public
    // document raycast retains its exact authored-world contract, including NoDraw tiles an editor may inspect.
    internal static TileHit? Pick(TileWorldDocument doc, int plane, Vector3 origin, Vector3 direction,
                                  float maxDistance, Func<int, int, int, bool> includeTile)
    {
        ArgumentNullException.ThrowIfNull(includeTile);
        return PickCore(doc, plane, origin, direction, maxDistance, includeTile);
    }

    static TileHit? PickCore(TileWorldDocument doc, int plane, Vector3 origin, Vector3 direction, float maxDistance,
                             Func<int, int, int, bool>? includeTile)
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
            if ((includeTile is null || includeTile(tx, tz, plane))
                && TestTile(doc, plane, tx, tz, origin, dir, maxDistance, out TileHit hit))
                return hit;
            if (vertical) return null;
            if (tMaxX < tMaxZ) { tx += stepX; travelled = tMaxX; tMaxX += tDeltaX; }
            else { tz += stepZ; travelled = tMaxZ; tMaxZ += tDeltaZ; }
        }
        return null;
    }

    static bool TestTile(TileWorldDocument doc, int plane, int tx, int tz, Vector3 origin, Vector3 dir, float maxDistance, out TileHit hit)
    {
        hit = default;
        Span<TileLatticeTriangle> triangles = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];
        if (!TileGroundTriangles.TryDescribeIncludingNoDraw(doc, tx, tz, plane, out TileGroundCell cell, triangles))
            return false;

        // The shared rule normalises winding in TILE space, and lattice positions are already in WORLD space
        // where z is negated, so the pair arrives wound the other way and its geometric normal points UP. Neither
        // direction reaches this loop: Intersect is two sided, and the mesher does not read the winding either, it
        // computes its normals from the height lattice.
        float best = float.PositiveInfinity;
        for (int i = 0; i < cell.TriangleCount; i++)
        {
            Vector3 a = TileGroundTriangles.LatticePosition(doc, tx, tz, plane, triangles[i].A, 0, 0);
            Vector3 b = TileGroundTriangles.LatticePosition(doc, tx, tz, plane, triangles[i].B, 0, 0);
            Vector3 c = TileGroundTriangles.LatticePosition(doc, tx, tz, plane, triangles[i].C, 0, 0);
            if (Intersect(origin, dir, a, b, c, out float t) && t < best) best = t;
        }
        if (float.IsPositiveInfinity(best) || best > maxDistance) return false;
        hit = new TileHit(tx, tz, plane, origin + dir * best, best);
        return true;
    }

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
