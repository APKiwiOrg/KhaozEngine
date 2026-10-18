using System;
using System.Numerics;
using BepuPhysics;
using BepuUtilities;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Complete corner bounds for the common affine solid and pinned rounded point graph.</summary>
internal static class InstalledSweepBounds
{
    internal static bool TryOperationalError(in InstalledSweepLeaf leaf, Vector3 centre,
        Vector3 displacement, float halfCylinderLength, out double error)
    {
        error = 0;
        InstalledPoseOperator map = leaf.Map;
        if (!map.RepresentedX.IsExact || !map.RepresentedY.IsExact || !map.RepresentedZ.IsExact) return false;
        Quaternion q = leaf.Pose.Orientation;
        var wide = new QuaternionWide { X = new(q.X), Y = new(q.Y), Z = new(q.Z), W = new(q.W) };
        QuaternionWide.Conjugate(wide, out QuaternionWide local);
        for (int component = 0; component < 3; component++)
        {
            Vector3Wide.Broadcast(component == 0 ? Vector3.UnitX : component == 1 ? Vector3.UnitY : Vector3.UnitZ, out var unit);
            QuaternionWide.TransformWithoutOverlap(unit, local, out var transformed);
            double x = Component(map.RepresentedX, component), y = Component(map.RepresentedY, component), z = Component(map.RepresentedZ, component);
            if (Math.Abs(x) >= 2 || Math.Abs(y) >= 2 || Math.Abs(z) >= 2) return false;
            for (int lane = 0; lane < Vector<float>.Count; lane++)
                if (transformed.X[lane] != x || transformed.Y[lane] != y || transformed.Z[lane] != z) return false;
        }
        double extent = 0;
        for (int component = 0; component < 3; component++)
        {
            double start = component == 0 ? centre.X : component == 1 ? centre.Y : centre.Z;
            double delta = component == 0 ? displacement.X : component == 1 ? displacement.Y : displacement.Z;
            double origin = component == 0 ? leaf.Pose.Position.X : component == 1 ? leaf.Pose.Position.Y : leaf.Pose.Position.Z;
            GeometryInterval first = GeometryInterval.Exact(start).Subtract(GeometryInterval.Exact(origin));
            GeometryInterval last = GeometryInterval.Exact(start).Add(GeometryInterval.Exact(delta)).Subtract(GeometryInterval.Exact(origin));
            if (!first.IsResolved || !last.IsResolved) return false;
            extent = Math.Max(extent, Math.Max(Math.Max(Math.Abs(first.Lower), Math.Abs(first.Upper)),
                Math.Max(Math.Abs(last.Lower), Math.Abs(last.Upper))));
        }
        // CapsuleBoxTester.Prepare and QuaternionWide.TransformWithoutOverlap in Bepu 2.4.0
        // use a rounded offset subtraction, three products and two sums. Coefficients above
        // are the actual source coefficients, with |a| < 2. If M bounds the complete offset,
        // its subtraction errors contribute at most 6 U(M), product inputs stay below 4 M,
        // and both sums stay below 16 M. U is the full neighboring binary32 spacing.
        // Rounded axis multiplication and addition contribute the final two terms for
        // every s in [-h,h]. Each sum is outward in binary64. No geometric radius is enlarged.
        GeometryInterval bound = Term(6, Spacing(extent)).Add(Term(3, Spacing(4 * extent)))
            .Add(Term(2, Spacing(16 * extent))).Add(Term(1, Spacing(2d * halfCylinderLength)))
            .Add(Term(1, Spacing(16 * (extent + halfCylinderLength))));
        if (!bound.IsResolved) return false;
        error = bound.Upper;
        return double.IsFinite(error);
    }

    static GeometryInterval Term(double count, double spacing) => GeometryInterval.Exact(count).Multiply(GeometryInterval.Exact(spacing));
    static double Component(FeaturePoint point, int component) => component == 0 ? point.X.Value : component == 1 ? point.Y.Value : point.Z.Value;
    static double Spacing(double value)
    {
        float represented = (float)value;
        return float.IsFinite(represented) ? (double)MathF.BitIncrement(represented) - MathF.BitDecrement(represented) : double.PositiveInfinity;
    }

    internal static bool TryCreate(InstalledSweepGeometry geometry, RigidPose pose,
        out Vector3 minimum, out Vector3 maximum)
    {
        minimum = maximum = default;
        if (!geometry.TryComposePose(pose, out RigidPose leafPose) ||
            !InstalledPoseOperator.TryCreate(leafPose, out InstalledPoseOperator map)) return false;
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        for (int i = 0; i < geometry.VertexCount; i++)
        {
            Vector3 point = geometry.Vertex(i);
            FeaturePoint affine = map.Point(point, exactZeroProducts: true);
            GeometryVector rounded = RepresentedGeometryTransforms.PosePoint(new Pose(leafPose.Position, leafPose.Orientation), point);
            if (!affine.IsResolved || !rounded.IsResolved) return false;
            min = Vector3.Min(min, new(Down(Math.Min(affine.X.Bounds.Lower, rounded.X.Lower)),
                Down(Math.Min(affine.Y.Bounds.Lower, rounded.Y.Lower)), Down(Math.Min(affine.Z.Bounds.Lower, rounded.Z.Lower))));
            max = Vector3.Max(max, new(Up(Math.Max(affine.X.Bounds.Upper, rounded.X.Upper)),
                Up(Math.Max(affine.Y.Bounds.Upper, rounded.Y.Upper)), Up(Math.Max(affine.Z.Bounds.Upper, rounded.Z.Upper))));
        }
        if (!InstalledPoseOperator.Within(min, CapsuleGeneralSweepPolicy.MaximumStoredCoordinate) ||
            !InstalledPoseOperator.Within(max, CapsuleGeneralSweepPolicy.MaximumStoredCoordinate)) return false;
        minimum = min; maximum = max;
        return true;
    }

    static float Down(double value)
    {
        float result = (float)value;
        return result > value ? MathF.BitDecrement(result) : result;
    }
    static float Up(double value)
    {
        float result = (float)value;
        return result < value ? MathF.BitIncrement(result) : result;
    }
}
