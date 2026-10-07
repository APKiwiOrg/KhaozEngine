using System;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Outward binary32 aperture for the complete original upright-capsule path.
/// Query enclosure does not establish the spatial tree's leaf bounds or candidate completeness.</summary>
internal static class CapsuleSweepAperture
{
    internal static bool TryCreate(Vector3 centre, float radius, float halfCylinderLength,
        Vector3 displacement, out Vector3 min, out Vector3 max)
    {
        min = max = default;
        if (!float.IsFinite(radius) || radius <= 0 ||
            !float.IsFinite(halfCylinderLength) || halfCylinderLength < 0) return false;
        GeometryVector start = CapsuleSweepPath.Point(centre, displacement, 0);
        GeometryVector end = CapsuleSweepPath.Point(centre, displacement, 1);
        if (!start.IsResolved || !end.IsResolved) return false;
        GeometryInterval radial = GeometryInterval.Exact(radius);
        GeometryInterval vertical = radial.Add(GeometryInterval.Exact(halfCylinderLength));
        if (!Axis(start.X, end.X, radial, out float minX, out float maxX) ||
            !Axis(start.Y, end.Y, vertical, out float minY, out float maxY) ||
            !Axis(start.Z, end.Z, radial, out float minZ, out float maxZ)) return false;
        min = new(minX, minY, minZ);
        max = new(maxX, maxY, maxZ);
        return true;
    }

    static bool Axis(GeometryInterval start, GeometryInterval end, GeometryInterval extent,
        out float min, out float max)
    {
        min = max = default;
        GeometryInterval hull = GeometryInterval.Enclose(Math.Min(start.Lower, end.Lower),
            Math.Max(start.Upper, end.Upper));
        GeometryInterval lower = hull.Subtract(extent).EncloseSingleRounding();
        GeometryInterval upper = hull.Add(extent).EncloseSingleRounding();
        if (!lower.IsResolved || !upper.IsResolved) return false;
        // EncloseSingleRounding supplies outward binary32 endpoints, including any lost low bits.
        min = (float)lower.Lower;
        max = (float)upper.Upper;
        return float.IsFinite(min) && float.IsFinite(max) && min <= max;
    }
}
