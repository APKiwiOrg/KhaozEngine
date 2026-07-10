using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapEditor;

/// <summary>Pure drag math for the transform gizmo: a drag is a pick ray each frame intersected with the
/// gesture's constraint surface. There is no state beyond the <see cref="DragGesture"/> struct passed in, so
/// every helper is a total function of its arguments (same input, same output) and allocation-free, which makes
/// the whole gizmo headless-testable. Rays are given as (origin, direction) with no normalization assumed, and
/// the handle dimensions come from <see cref="GizmoGeometry"/> so the pickable region matches the drawn mesh.</summary>
public static class GizmoDrag
{
    /// <summary>The handle a gizmo pick grabbed, or <see cref="None"/>. A single flat-drag handle
    /// (<see cref="TranslateXZ"/>) covers both ground-plane arrows.</summary>
    public enum GizmoHandle
    {
        /// <summary>No handle under the ray.</summary>
        None,
        /// <summary>Either ground-plane arrow (+X or +Z): translate on the y = start plane.</summary>
        TranslateXZ,
        /// <summary>The +Y arrow: translate up/down the vertical axis.</summary>
        TranslateY,
        /// <summary>The flat ring: yaw about the vertical axis.</summary>
        YawRing,
        /// <summary>The corner cube: uniform scale.</summary>
        Scale,
    }

    /// <summary>The immutable state captured when a drag begins: which <paramref name="Handle"/> was grabbed, the
    /// world <paramref name="StartPoint"/> the ray first hit on the constraint surface, and the object's transform
    /// at that instant (<paramref name="ObjectStart"/> position, <paramref name="ObjectStartYaw"/> radians,
    /// <paramref name="ObjectStartScale"/>). The caller composes a per-frame result from a start value plus a
    /// delta, e.g. <c>newYaw = ObjectStartYaw + YawDelta(...)</c> and <c>newScale = ObjectStartScale *
    /// ScaleFactor(...)</c>.</summary>
    public readonly record struct DragGesture(GizmoHandle Handle, Vector3 StartPoint, Vector3 ObjectStart,
        float ObjectStartYaw, float ObjectStartScale);

    /// <summary>Which handle a pick ray grabs at the gizmo's world position and screen-constant scale. Pure
    /// AABB tests (via <see cref="RayMath.IntersectAabb"/>) for the arrows and cube, and a flat annulus band test
    /// for the ring. When volumes overlap the highest priority wins, in order: <see cref="GizmoHandle.Scale"/>,
    /// then <see cref="GizmoHandle.TranslateY"/>, then the <see cref="GizmoHandle.TranslateXZ"/> arrows, then
    /// <see cref="GizmoHandle.YawRing"/> (so grabbing the cube or the vertical arrow is never stolen by an arrow
    /// or ring they overlap near the origin).</summary>
    public static GizmoHandle HitTest(Vector3 gizmoPos, float gizmoScale, Vector3 origin, Vector3 dir)
    {
        if (HitScaleCube(gizmoPos, gizmoScale, origin, dir)) return GizmoHandle.Scale;

        float hw = GizmoGeometry.ArrowHalfWidth;
        float len = GizmoGeometry.ArrowLength;
        if (HitArrow(gizmoPos, gizmoScale, origin, dir,
                new Vector3(-hw, 0f, -hw), new Vector3(hw, len, hw)))
            return GizmoHandle.TranslateY;
        if (HitArrow(gizmoPos, gizmoScale, origin, dir,
                new Vector3(0f, -hw, -hw), new Vector3(len, hw, hw)) ||
            HitArrow(gizmoPos, gizmoScale, origin, dir,
                new Vector3(-hw, -hw, 0f), new Vector3(hw, hw, len)))
            return GizmoHandle.TranslateXZ;

        if (HitYawRing(gizmoPos, gizmoScale, origin, dir)) return GizmoHandle.YawRing;

        return GizmoHandle.None;
    }

    /// <summary>Ground-plane translation: the current ray intersected with the horizontal plane at
    /// <c>StartPoint.Y</c>, minus the start point. Zero when the ray is parallel to (or points away from) the
    /// plane, so no valid intersection means no movement.</summary>
    public static Vector3 TranslateXZDelta(in DragGesture g, Vector3 origin, Vector3 dir)
    {
        return IntersectHorizontalPlane(origin, dir, g.StartPoint.Y, out Vector3 hit)
            ? hit - g.StartPoint
            : Vector3.Zero;
    }

    /// <summary>Vertical translation: the signed Y change of the closest approach between the pick ray and the
    /// vertical axis through <c>StartPoint</c>. Zero when the ray runs parallel to the vertical axis (no unique
    /// closest point).</summary>
    public static float TranslateYDelta(in DragGesture g, Vector3 origin, Vector3 dir)
    {
        // Closest point on line A (axis through StartPoint, direction +Y) to line B (the ray).
        // sc = (b*e - c*d) / (a*c - b*b); the axis is unit +Y so the returned parameter is exactly the Y delta.
        Vector3 axisDir = Vector3.UnitY;
        Vector3 w0 = g.StartPoint - origin;
        float a = 1f;                              // axisDir . axisDir
        float b = Vector3.Dot(axisDir, dir);
        float c = Vector3.Dot(dir, dir);
        float d = Vector3.Dot(axisDir, w0);
        float e = Vector3.Dot(dir, w0);
        float denom = a * c - b * b;
        if (MathF.Abs(denom) < 1e-9f) return 0f;   // ray parallel to the vertical axis
        return (b * e - c * d) / denom;
    }

    /// <summary>Yaw about the gizmo: the signed ground-plane angle swept from the start handle direction to where
    /// the current ray meets the y = <c>StartPoint.Y</c> plane, wrapped to (-pi, pi]. Zero when the ray does not
    /// meet the plane.</summary>
    public static float YawDelta(in DragGesture g, Vector3 origin, Vector3 dir)
    {
        if (!IntersectHorizontalPlane(origin, dir, g.StartPoint.Y, out Vector3 hit)) return 0f;
        float start = AngleAround(g.ObjectStart, g.StartPoint);
        float now = AngleAround(g.ObjectStart, hit);
        float d = now - start;
        return MathF.Atan2(MathF.Sin(d), MathF.Cos(d)); // shortest signed wrap to (-pi, pi]
    }

    /// <summary>Uniform scale factor: the ratio of the current ground-plane radius (where the ray meets the
    /// y = <c>StartPoint.Y</c> plane) to the start radius, both measured from <c>ObjectStart</c>. One (no change)
    /// when the start radius is degenerate or the ray does not meet the plane.</summary>
    public static float ScaleFactor(in DragGesture g, Vector3 origin, Vector3 dir)
    {
        if (!IntersectHorizontalPlane(origin, dir, g.StartPoint.Y, out Vector3 hit)) return 1f;
        float startR = RadialXZ(g.ObjectStart, g.StartPoint);
        if (startR < 1e-6f) return 1f;
        return RadialXZ(g.ObjectStart, hit) / startR;
    }

    static bool HitScaleCube(Vector3 gizmoPos, float s, Vector3 origin, Vector3 dir)
    {
        Vector3 center = gizmoPos + new Vector3(GizmoGeometry.ScaleCubeOffset, 0f, GizmoGeometry.ScaleCubeOffset) * s;
        Vector3 half = new Vector3(GizmoGeometry.ScaleCubeHalfExtent) * s;
        return RayMath.IntersectAabb(origin, dir, center - half, center + half, out _);
    }

    static bool HitArrow(Vector3 gizmoPos, float s, Vector3 origin, Vector3 dir, Vector3 localMin, Vector3 localMax)
    {
        Vector3 min = gizmoPos + localMin * s;
        Vector3 max = gizmoPos + localMax * s;
        return RayMath.IntersectAabb(origin, dir, min, max, out _);
    }

    static bool HitYawRing(Vector3 gizmoPos, float s, Vector3 origin, Vector3 dir)
    {
        if (!IntersectHorizontalPlane(origin, dir, gizmoPos.Y, out Vector3 hit)) return false;
        float r = RadialXZ(gizmoPos, hit);
        return MathF.Abs(r - GizmoGeometry.RingRadius * s) <= GizmoGeometry.RingBandHalfWidth * s;
    }

    /// <summary>Intersects a ray with the horizontal plane y = <paramref name="planeY"/>. Returns false (and a
    /// default point) when the ray is parallel to the plane or the intersection is behind the origin.</summary>
    static bool IntersectHorizontalPlane(Vector3 origin, Vector3 dir, float planeY, out Vector3 hit)
    {
        if (dir.Y == 0f) { hit = default; return false; }
        float t = (planeY - origin.Y) / dir.Y;
        if (t < 0f) { hit = default; return false; }
        hit = origin + dir * t;
        return true;
    }

    /// <summary>Ground-plane (XZ) angle of <paramref name="p"/> around <paramref name="center"/>, as
    /// atan2(dz, dx), so +X is angle 0 and +Z is +pi/2.</summary>
    static float AngleAround(Vector3 center, Vector3 p) => MathF.Atan2(p.Z - center.Z, p.X - center.X);

    /// <summary>Ground-plane (XZ) distance from <paramref name="center"/> to <paramref name="p"/>.</summary>
    static float RadialXZ(Vector3 center, Vector3 p)
    {
        float dx = p.X - center.X;
        float dz = p.Z - center.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
