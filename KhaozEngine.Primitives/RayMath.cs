using System.Numerics;

namespace KhaozEngine.Primitives;

/// <summary>Allocation-free 3D ray intersection helpers (System.Numerics). Zero-dependency leaf math
/// used by editor picking and any future spatial query. Directions need not be normalized: t values
/// are in units of the direction's length.</summary>
public static class RayMath
{
    /// <summary>Slab test against an axis-aligned box. Returns true when the ray hits the box at
    /// t >= 0, with tNear the entry distance (0 when the origin starts inside).</summary>
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

    private static bool SlabAxis(float origin, float direction, float min, float max, ref float near, ref float far)
    {
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
