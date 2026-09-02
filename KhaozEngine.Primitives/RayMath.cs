using System;
using System.Numerics;

namespace KhaozEngine.Primitives;

/// <summary>Allocation-free 3D ray intersection helpers (System.Numerics). Zero-dependency leaf math
/// used by editor picking and any future spatial query. Directions need not be normalized: t values
/// are in units of the direction's length.</summary>
public static class RayMath
{
    /// <summary>Slab test against an axis-aligned box. Returns true when the ray hits the box at
    /// t >= 0, with tNear the entry distance (0 when the origin starts inside). A degenerate zero-length
    /// ray (direction is the zero vector) hits, at tNear 0, only when the origin already lies inside the
    /// box on every axis, and otherwise misses. A NaN component in either <paramref name="origin"/> or
    /// <paramref name="direction"/> always misses (a NaN comparison is false on both sides, so without an
    /// explicit check it would silently fall through every slab test as an always-pass hit instead).</summary>
    public static bool IntersectAabb(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, out float tNear)
    {
        float near = float.NegativeInfinity;
        float far = float.PositiveInfinity;

        if (!SlabAxis(origin.X, direction.X, min.X, max.X, ref near, ref far) ||
            !SlabAxis(origin.Y, direction.Y, min.Y, max.Y, ref near, ref far) ||
            !SlabAxis(origin.Z, direction.Z, min.Z, max.Z, ref near, ref far))
        {
            tNear = 0f;
            return false;
        }

        if (far < 0f || near > far)
        {
            tNear = 0f;
            return false;
        }

        tNear = near > 0f ? near : 0f;
        return true;
    }

    /// <summary>Slab test against a box that is axis-aligned in its OWN frame and yawed about world Y, the
    /// shape every placed prop, actor and clickbox in a Y-up world has. <paramref name="center"/> is the box's
    /// world anchor and <paramref name="min"/>/<paramref name="max"/> are its extents in the box's local frame
    /// (so they are relative to the anchor, not world coordinates). The ray is untranslated by the anchor and
    /// unrotated by <paramref name="yaw"/> radians, then handed to <see cref="IntersectAabb"/>, so every edge
    /// case that test pins (inside-origin tNear 0, zero-length ray, NaN miss) holds here unchanged and
    /// <paramref name="tNear"/> stays in units of the direction's length. A yaw of 0 is the same answer as
    /// calling <see cref="IntersectAabb"/> with the anchor subtracted out.</summary>
    public static bool IntersectObbY(
        Vector3 origin, Vector3 direction, Vector3 center, float yaw, Vector3 min, Vector3 max, out float tNear)
    {
        // Rotating the ray by -yaw is the same picking answer as rotating the box by +yaw, and it keeps the
        // actual intersection on the cheap axis-aligned path.
        float cos = MathF.Cos(-yaw);
        float sin = MathF.Sin(-yaw);
        Vector3 ro = origin - center;
        var localOrigin = new Vector3(ro.X * cos + ro.Z * sin, ro.Y, -ro.X * sin + ro.Z * cos);
        var localDirection = new Vector3(
            direction.X * cos + direction.Z * sin, direction.Y, -direction.X * sin + direction.Z * cos);
        return IntersectAabb(localOrigin, localDirection, min, max, out tNear);
    }

    private static bool SlabAxis(float origin, float direction, float min, float max, ref float near, ref float far)
    {
        if (float.IsNaN(origin) || float.IsNaN(direction))
        {
            // Every comparison against NaN is false, so leaving this to the branches below would divide out to
            // NaN t-values, skip both the near/far updates (their guarding comparisons are also false), and return
            // true unconditionally - an always-pass slab. Treat a NaN component as a definite miss instead.
            return false;
        }

        if (direction == 0f)
        {
            // Axis-parallel: the ray never leaves this slab, so it is a hit only when the
            // origin already lies within [min, max] on this axis. No division, no NaN risk.
            return origin >= min && origin <= max;
        }

        float inverse = 1f / direction;
        float t1 = (min - origin) * inverse;
        float t2 = (max - origin) * inverse;

        if (t1 > t2)
        {
            (t1, t2) = (t2, t1);
        }

        if (t1 > near)
        {
            near = t1;
        }

        if (t2 < far)
        {
            far = t2;
        }

        return true;
    }
}
