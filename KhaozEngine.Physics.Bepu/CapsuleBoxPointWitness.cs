using System;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Exact sign of capsule distance to an installed identity box at a supplied point.
/// It does not prove that this point belongs to a swept path, or that every world candidate was examined.</summary>
internal static class CapsuleBoxPointWitness
{
    internal static GeometrySign Classify(Shapes shapes, TypedIndex shape, RigidPose pose,
        ReadOnlySpan<double> centre, float radius, float halfCylinderLength)
    {
        if (centre.Length != 3 || !float.IsFinite(radius) || radius <= 0f ||
            !float.IsFinite(halfCylinderLength) || halfCylinderLength < 0f ||
            !CapsuleSweepGeometry.TryReadIdentityBox(shapes, shape, pose, out Box box))
            return GeometrySign.Unresolved;

        Span<double> origin = stackalloc double[] { pose.Position.X, pose.Position.Y, pose.Position.Z };
        Span<double> local = stackalloc double[3];
        for (int i = 0; i < 3; i++)
            if (!TryExactDifference(centre[i], origin[i], out local[i])) return GeometrySign.Unresolved;

        GeometryInterval expanded = ExpandedY(box.HalfHeight, halfCylinderLength);
        if (!expanded.IsResolved || expanded.Lower != expanded.Upper) return GeometrySign.Unresolved;
        double expandedY = expanded.Lower;

        Span<double> extent = stackalloc double[] { box.HalfWidth, expandedY, box.HalfLength };
        Span<double> closest = stackalloc double[3];
        for (int i = 0; i < 3; i++) closest[i] = Math.Clamp(local[i], -extent[i], extent[i]);
        // Vertical-axis/box distance equals point distance to the box expanded along Y by the axis half-length.
        // Clamp selects existing represented coordinates, so no rounded coordinate arithmetic is hidden here.
        return BoundedGeometryArithmetic.CompareSquaredDistances(local, closest, [radius], [0d]);
    }

    /// <summary>Classifies every point in the supplied enclosure. A straddling enclosure is unresolved.
    /// Exact tangency requires every distance contribution to be independently proved.</summary>
    internal static GeometrySign ClassifyEnclosure(Shapes shapes, TypedIndex shape, RigidPose pose,
        GeometryVector centre, float radius, float halfCylinderLength)
    {
        if (!centre.IsResolved || !float.IsFinite(radius) || radius <= 0f ||
            !float.IsFinite(halfCylinderLength) || halfCylinderLength < 0f ||
            !CapsuleSweepGeometry.TryReadIdentityBox(shapes, shape, pose, out Box box))
            return GeometrySign.Unresolved;

        Span<GeometryInterval> world = stackalloc GeometryInterval[] { centre.X, centre.Y, centre.Z };
        Span<double> origin = stackalloc double[] { pose.Position.X, pose.Position.Y, pose.Position.Z };
        Span<GeometryInterval> extent = stackalloc GeometryInterval[]
        {
            GeometryInterval.Exact(box.HalfWidth), ExpandedY(box.HalfHeight, halfCylinderLength),
            GeometryInterval.Exact(box.HalfLength)
        };
        Span<double> exactLocal = stackalloc double[3];
        Span<double> exactClosest = stackalloc double[3];
        bool exactContributions = true;
        GeometryInterval squaredDistance = GeometryInterval.Exact(0);
        for (int i = 0; i < 3; i++)
        {
            GeometryInterval local = world[i].Lower == world[i].Upper &&
                TryExactDifference(world[i].Lower, origin[i], out double difference)
                ? GeometryInterval.Exact(difference) : world[i].Subtract(GeometryInterval.Exact(origin[i]));
            GeometryInterval right = local.Subtract(extent[i]);
            GeometryInterval left = GeometryInterval.Exact(0).Subtract(local).Subtract(extent[i]);
            if (!right.IsResolved || !left.IsResolved) return GeometrySign.Unresolved;
            GeometryInterval distance = GeometryInterval.Enclose(
                Math.Max(0, Math.Max(right.Lower, left.Lower)),
                Math.Max(0, Math.Max(right.Upper, left.Upper)));
            squaredDistance = squaredDistance.Add(distance.Square());
            if (distance.Upper == 0)
            {
                // Every enclosed coordinate is interior, so this contribution is exactly zero.
                exactLocal[i] = exactClosest[i] = 0;
            }
            else if (local.Lower == local.Upper && extent[i].Lower == extent[i].Upper)
            {
                exactLocal[i] = local.Lower;
                exactClosest[i] = Math.Clamp(local.Lower, -extent[i].Lower, extent[i].Lower);
            }
            else exactContributions = false;
        }
        GeometryInterval comparison = squaredDistance.Subtract(GeometryInterval.Exact(radius).Square());
        if (!comparison.IsResolved) return GeometrySign.Unresolved;
        if (comparison.Upper < 0) return GeometrySign.Negative;
        if (comparison.Lower > 0) return GeometrySign.Positive;
        return exactContributions
            ? BoundedGeometryArithmetic.CompareSquaredDistances(exactLocal, exactClosest, [radius], [0d])
            : GeometrySign.Unresolved;
    }

    static GeometryInterval ExpandedY(float halfHeight, float halfCylinderLength)
    {
        double proposed = (double)halfHeight + halfCylinderLength;
        // Both addends are nonnegative. Squared equality plus the sign proves the proposed sum.
        return double.IsFinite(proposed) && proposed >= halfHeight &&
            BoundedGeometryArithmetic.CompareSquaredDistances([proposed], [halfHeight],
                [halfCylinderLength], [0d]) == GeometrySign.Zero
            ? GeometryInterval.Exact(proposed)
            : GeometryInterval.Exact(halfHeight).Add(GeometryInterval.Exact(halfCylinderLength));
    }

    static bool TryExactDifference(double a, double b, out double difference)
    {
        difference = default;
        if (!double.IsFinite(a) || !double.IsFinite(b)) return false;
        double proposed = a - b;
        if (!double.IsFinite(proposed) || Math.Sign(proposed) != Math.Sign(a.CompareTo(b)) ||
            BoundedGeometryArithmetic.CompareSquaredDistances([a], [b], [proposed], [0d]) != GeometrySign.Zero)
            return false;
        difference = proposed;
        return true;
    }
}
