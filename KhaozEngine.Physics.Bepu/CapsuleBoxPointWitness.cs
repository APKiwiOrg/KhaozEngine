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

        double expandedY = (double)box.HalfHeight + halfCylinderLength;
        // Both addends are nonnegative. Exact squared equality plus the sign proves this proposed sum.
        if (!double.IsFinite(expandedY) || expandedY < box.HalfHeight ||
            BoundedGeometryArithmetic.CompareSquaredDistances([expandedY], [box.HalfHeight],
                [halfCylinderLength], [0d]) != GeometrySign.Zero)
            return GeometrySign.Unresolved;

        Span<double> extent = stackalloc double[] { box.HalfWidth, expandedY, box.HalfLength };
        Span<double> closest = stackalloc double[3];
        for (int i = 0; i < 3; i++) closest[i] = Math.Clamp(local[i], -extent[i], extent[i]);
        // Vertical-axis/box distance equals point distance to the box expanded along Y by the axis half-length.
        // Clamp selects existing represented coordinates, so no rounded coordinate arithmetic is hidden here.
        return BoundedGeometryArithmetic.CompareSquaredDistances(local, closest, [radius], [0d]);
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
