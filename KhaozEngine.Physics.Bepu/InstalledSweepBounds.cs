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
        if (!OperationalCoefficientsMatch(map, leaf.Pose.Orientation)) return false;
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
        GeometryInterval bound = BaseOperationalError(extent, halfCylinderLength);
        if (leaf.Geometry.LocalPose.Position != Vector3.Zero)
        {
            // Mesh and hull recentering add the rotated child offset after the original relative
            // root path in ConvexSweepTaskCommon.ConstructSamples. A composed world pose
            // adds it before subtracting the query. Cancellation can make those intermediate
            // magnitudes much larger than the final leaf offset, so account for them explicitly.
            RigidPose root = leaf.Geometry.ParentPose;
            if (!InstalledPoseOperator.TryCreate(root, out var rootMap)) return false;
            GeometryVector offset = rootMap.RoundedDirection(FeaturePoint.Exact(leaf.Geometry.LocalPose.Position).Bounds);
            if (!offset.IsResolved) return false;
            double h = Math.Max(Math.Max(Math.Abs(offset.X.Lower), Math.Abs(offset.X.Upper)),
                Math.Max(Math.Max(Math.Abs(offset.Y.Lower), Math.Abs(offset.Y.Upper)), Math.Max(Math.Abs(offset.Z.Lower), Math.Abs(offset.Z.Upper))));
            double m = 0, p = 0;
            for (int component = 0; component < 3; component++)
            {
                double start = component == 0 ? centre.X : component == 1 ? centre.Y : centre.Z;
                double delta = component == 0 ? displacement.X : component == 1 ? displacement.Y : displacement.Z;
                double origin = component == 0 ? root.Position.X : component == 1 ? root.Position.Y : root.Position.Z;
                GeometryInterval first = GeometryInterval.Exact(origin).Subtract(GeometryInterval.Exact(start));
                GeometryInterval last = first.Subtract(GeometryInterval.Exact(delta));
                if (!first.IsResolved || !last.IsResolved) return false;
                m = Math.Max(m, Math.Max(Math.Max(Math.Abs(first.Lower), Math.Abs(first.Upper)), Math.Max(Math.Abs(last.Lower), Math.Abs(last.Upper))));
                p = Math.Max(p, Math.Abs(origin));
            }
            // One initial subtraction, one displacement product, one path sum, one
            // child sum and the composed-pose sum. Propagate that offset error through
            // the actual absolute transpose row sums, retaining outward accumulation.
            GeometryInterval extra = ChildOperationalError(m, h, p);
            double norm = 0;
            for (int rowIndex = 0; rowIndex < 3; rowIndex++)
            {
                FeaturePoint row = rowIndex == 0 ? map.RepresentedX : rowIndex == 1 ? map.RepresentedY : map.RepresentedZ;
                GeometryInterval sum = GeometryInterval.Exact(Math.Abs(row.X.Value)).Add(GeometryInterval.Exact(Math.Abs(row.Y.Value)))
                    .Add(GeometryInterval.Exact(Math.Abs(row.Z.Value)));
                if (!sum.IsResolved) return false;
                norm = Math.Max(norm, sum.Upper);
            }
            bound = bound.Add(extra.Multiply(GeometryInterval.Exact(norm)));
        }
        if (!bound.IsResolved) return false;
        error = bound.Upper;
        return double.IsFinite(error);
    }

    internal static bool OperationalCoefficientsMatch(InstalledPoseOperator map, Quaternion q)
    {
        if (!map.RepresentedX.IsExact || !map.RepresentedY.IsExact || !map.RepresentedZ.IsExact) return false;
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
        return true;
    }

    internal static GeometryInterval BaseOperationalError(double extent, float half) =>
        Term(6, Spacing(extent)).Add(Term(3, Spacing(4 * extent)))
            .Add(Term(2, Spacing(16 * extent))).Add(Term(1, Spacing(2d * half)))
            .Add(Term(1, Spacing(16 * (extent + half))));

    internal static GeometryInterval ChildOperationalError(double m, double h, double p) =>
        Term(1, Spacing(m)).Add(Term(1, Spacing(2 * m)))
            .Add(Term(1, Spacing(4 * m))).Add(Term(1, Spacing(4 * m + h))).Add(Term(1, Spacing(p + h)));

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
            !InstalledPoseOperator.TryCreate(leafPose, out InstalledPoseOperator map) || !CapsuleSweepPruning.Validate(map, leafPose.Orientation)) return false;
        if (geometry.Children is { } children)
        {
            Vector3 childMin = new(float.PositiveInfinity), childMax = new(float.NegativeInfinity);
            Vector3 pruningMin = childMin, pruningMax = childMax;
            foreach (var child in children)
            {
                if (!TryCreate(child, pose, out var lo, out var hi)) return false;
                childMin = Vector3.Min(childMin, lo); childMax = Vector3.Max(childMax, hi);
                if (child.PruningBounds is not { } bounds) return false;
                pruningMin = Vector3.Min(pruningMin, bounds.Min); pruningMax = Vector3.Max(pruningMax, bounds.Max);
            }
            geometry.PruningBounds = (pruningMin, pruningMax);
            minimum = childMin; maximum = childMax;
            return children.Length > 0 && children.Length <= CapsuleGeneralSweepPolicy.MaximumChildren;
        }
        if (geometry.Mesh is { } mesh)
        {
            if (!mesh.ProvePose(pose, map)) return false;
            geometry.PruningBounds = mesh.PruningBounds;
            (minimum, maximum) = mesh.RootBounds;
            return InstalledPoseOperator.Within(minimum, CapsuleGeneralSweepPolicy.MaximumStoredCoordinate) &&
                InstalledPoseOperator.Within(maximum, CapsuleGeneralSweepPolicy.MaximumStoredCoordinate);
        }
        if (!CapsuleSweepPruning.TryStoredPad(pose, leafPose, geometry.LocalPose.Position, out double pad)) return false;
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        if (geometry.Curved.HasValue)
        {
            // A local box encloses the full circular radial set, without replacing the
            // leaf's distance geometry. Also enclose the unscaled spherical skin used by
            // the round segment metric. These are broad-phase bounds only.
            float extent = Up((double)geometry.Radius + geometry.HalfLength);
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = new((corner & 1) == 0 ? -extent : extent,
                    (corner & 2) == 0 ? -extent : extent, (corner & 4) == 0 ? -extent : extent);
                if (!TryPoint(map, leafPose, point, out var lo, out var hi)) return false;
                min = Vector3.Min(min, lo); max = Vector3.Max(max, hi);
            }
            Vector3 r = new(geometry.Radius);
            for (int i = 0; i < geometry.VertexCount; i++)
            {
                if (!TryPoint(map, leafPose, geometry.Vertex(i), out var lo, out var hi)) return false;
                min = Vector3.Min(min, new(Down((double)lo.X - r.X), Down((double)lo.Y - r.Y), Down((double)lo.Z - r.Z)));
                max = Vector3.Max(max, new(Up((double)hi.X + r.X), Up((double)hi.Y + r.Y), Up((double)hi.Z + r.Z)));
            }
            geometry.PruningBounds = (min, max);
            return CapsuleSweepPruning.ExpandStored(min, max, pad, out minimum, out maximum);
        }
        for (int i = 0; i < geometry.VertexCount; i++)
        {
            if (!TryPoint(map, leafPose, geometry.Vertex(i), out var lo, out var hi)) return false;
            min = Vector3.Min(min, lo); max = Vector3.Max(max, hi);
        }
        geometry.PruningBounds = (min, max);
        return CapsuleSweepPruning.ExpandStored(min, max, pad, out minimum, out maximum);
    }

    internal static bool TryPoint(InstalledPoseOperator map, RigidPose pose, Vector3 point,
        out Vector3 minimum, out Vector3 maximum)
    {
        minimum = maximum = default;
        FeaturePoint affine = map.Point(point, exactZeroProducts: true);
        GeometryVector rounded = RepresentedGeometryTransforms.PosePoint(new Pose(pose.Position, pose.Orientation), point);
        // The source tests distance after the transpose map. Its preimage is the inverse
        // transpose, which need not equal the forward quaternion map. Rounded root/child
        // arithmetic is separately enclosed by this leaf's mutation-time stored pad.
        FeaturePoint operational = CapsuleSweepPruning.OperationalPoint(map, point);
        if (!affine.IsResolved || !rounded.IsResolved || !operational.IsResolved) return false;
        minimum = new(Down(Math.Min(affine.X.Bounds.Lower, rounded.X.Lower)),
            Down(Math.Min(affine.Y.Bounds.Lower, rounded.Y.Lower)), Down(Math.Min(affine.Z.Bounds.Lower, rounded.Z.Lower)));
        maximum = new(Up(Math.Max(affine.X.Bounds.Upper, rounded.X.Upper)),
            Up(Math.Max(affine.Y.Bounds.Upper, rounded.Y.Upper)), Up(Math.Max(affine.Z.Bounds.Upper, rounded.Z.Upper)));
        minimum = Vector3.Min(minimum, new(Down(operational.X.Bounds.Lower), Down(operational.Y.Bounds.Lower), Down(operational.Z.Bounds.Lower)));
        maximum = Vector3.Max(maximum, new(Up(operational.X.Bounds.Upper), Up(operational.Y.Bounds.Upper), Up(operational.Z.Bounds.Upper)));
        return InstalledPoseOperator.Within(minimum, CapsuleGeneralSweepPolicy.MaximumStoredCoordinate) &&
            InstalledPoseOperator.Within(maximum, CapsuleGeneralSweepPolicy.MaximumStoredCoordinate);
    }

    internal static float Down(double value)
    {
        float result = (float)value;
        return result > value ? MathF.BitDecrement(result) : result;
    }
    internal static float Up(double value)
    {
        float result = (float)value;
        return result < value ? MathF.BitIncrement(result) : result;
    }
}
