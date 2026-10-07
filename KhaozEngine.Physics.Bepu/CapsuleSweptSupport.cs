using System;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Encloses an upright capsule's whole-path minimum projection minus a supplied solid maximum.
/// The solid interval is a premise in the same frame/axis, not geometry established by this helper.
/// Only a resolved positive lower gap proves separation. Other results do not prove a collision.</summary>
internal static class CapsuleSweptSupport
{
    internal static GeometryInterval Evaluate(Vector3 centre, float radius, float halfCylinderLength,
        Vector3 displacement, Vector3 axis, GeometryInterval maximumSolidProjection)
    {
        if (!maximumSolidProjection.IsResolved || !Finite(centre) || !Finite(displacement) ||
            !Finite(centre + displacement) || !Finite(axis) || axis == Vector3.Zero ||
            !float.IsFinite(radius) || radius <= 0f ||
            !float.IsFinite(halfCylinderLength) || halfCylinderLength < 0f)
            return default;

        GeometryInterval axisLength = GeometryInterval.Exact(axis.X).Square()
            .Add(GeometryInterval.Exact(axis.Y).Square())
            .Add(GeometryInterval.Exact(axis.Z).Square()).Sqrt();
        GeometryInterval radialExtent = GeometryInterval.Exact(radius).Multiply(axisLength);
        GeometryInterval axialExtent = GeometryInterval.Exact(halfCylinderLength)
            .Multiply(GeometryInterval.Exact(Math.Abs(axis.Y)));
        GeometryInterval travelProjection = Dot(axis, displacement);
        if (!travelProjection.IsResolved) return default;
        // Minimum over the entire closed translation interval, not sampled endpoint clearance.
        // min(0,x) is monotone, so its image of an enclosure uses both enclosed endpoints.
        GeometryInterval travelMinimum = GeometryInterval.Enclose(
            Math.Min(0d, travelProjection.Lower), Math.Min(0d, travelProjection.Upper));
        return Dot(axis, centre).Subtract(axialExtent).Subtract(radialExtent)
            .Add(travelMinimum).Subtract(maximumSolidProjection);
    }

    static GeometryInterval Dot(Vector3 a, Vector3 b) =>
        GeometryInterval.Exact(a.X).Multiply(GeometryInterval.Exact(b.X))
            .Add(GeometryInterval.Exact(a.Y).Multiply(GeometryInterval.Exact(b.Y)))
            .Add(GeometryInterval.Exact(a.Z).Multiply(GeometryInterval.Exact(b.Z)));

    static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
