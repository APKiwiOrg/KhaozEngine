using System;
using System.Numerics;

namespace KhaozEngine.TileWorld;

/// <summary>A ray hit on the ground lattice.</summary>
public readonly record struct TileHit(int X, int Z, int Plane, Vector3 Point, float Distance);

/// <summary>Ray against the tile lattice, GPU-free, so the editor's click and the game's click-to-walk share
/// it. World units are tiles times <see cref="TileWorldDocument.TileSize"/> on x/z and metres on y.</summary>
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

        // 2D DDA over tiles in XZ.
        float px = origin.X / ts, pz = origin.Z / ts;
        int tx = (int)MathF.Floor(px), tz = (int)MathF.Floor(pz);
        float dx = dir.X / ts, dz = dir.Z / ts;
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
        var sw = new Vector3(tx * ts, h00 * 0.01f, tz * ts);
        var se = new Vector3((tx + 1) * ts, h10 * 0.01f, tz * ts);
        var nw = new Vector3(tx * ts, h01 * 0.01f, (tz + 1) * ts);
        var ne = new Vector3((tx + 1) * ts, h11 * 0.01f, (tz + 1) * ts);
        bool swne = TileTriangulation.SplitSwNe(h00, h10, h01, h11, doc.GetOverlayShape(tx, tz, plane), doc.GetOverlayRotation(tx, tz, plane));

        // This vertex order winds a DOWNWARD normal, harmless here because Intersect is two-sided. A mesher
        // that back-face culls must wind the other way round.
        float best = float.PositiveInfinity;
        if (swne)
        {
            if (Intersect(origin, dir, sw, se, ne, out float t0) && t0 < best) best = t0;
            if (Intersect(origin, dir, sw, ne, nw, out float t1) && t1 < best) best = t1;
        }
        else
        {
            if (Intersect(origin, dir, sw, se, nw, out float t0) && t0 < best) best = t0;
            if (Intersect(origin, dir, se, ne, nw, out float t1) && t1 < best) best = t1;
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
