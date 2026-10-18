using System;
using System.Numerics;
using BepuPhysics.Collidables;

namespace KhaozEngine.Physics.Bepu;

/// <summary>A finite open triangle uses the convex distance strata without solid occupancy.
/// Initial contact is a separate one-sided contact question. Later raw sweeps use the pinned
/// GJK triangle support, which is two-sided unless the engine's directional filter excludes it.</summary>
internal static class CapsuleTriangleSweep
{
    internal static CapsuleSweepResult Sweep(in InstalledSweepLeaf triangle, Vector3 centre,
        float radius, float halfCylinderLength, Vector3 displacement, QueryFilter filter,
        float maximumErrorMetres, ref CapsuleSweepWork work, CapsuleSweepScratch scratch)
    {
        if (!work.Primitive() || !triangle.Geometry.IsTriangle || !Nondegenerate(triangle.Geometry) ||
            triangle.Geometry.SweepCompanion is not { } companion || !Nondegenerate(companion) || filter.Layers != 0) return default;
        // Complete distance already distinguishes an initial closed event from later
        // contact. Reusing that proof avoids spending the primitive cell account twice.
        CapsuleSweepResult distance = Distance(triangle, centre, radius, halfCylinderLength,
            displacement, maximumErrorMetres, ref work, scratch);
        if (!distance.IsComplete) return default;
        if (distance.Status == CapsuleSweepStatus.Hit && distance.ClearThroughDistance == 0)
        {
            // Direction does not remove an existing front contact. A bracket that merely
            // starts at zero is not a stationary witness and must not become Hit(0).
            if (distance.ImpactDistance == 0)
                return FrontAxis(triangle, centre, halfCylinderLength) ? distance : default;
        }
        if (displacement == Vector3.Zero) return distance;
        if (filter.CullBackFaces)
        {
            if (!Direction(triangle, displacement, out bool culled)) return default;
            if (culled) return distance.Status == CapsuleSweepStatus.Hit && distance.ClearThroughDistance == 0
                ? default : Clear(displacement);
        }
        return distance;
    }

    static CapsuleSweepResult Distance(in InstalledSweepLeaf triangle, Vector3 centre, float radius,
        float half, Vector3 delta, float maximumError, ref CapsuleSweepWork work, CapsuleSweepScratch scratch)
    {
        CapsuleSweepResult original = CapsuleConvexSweep.Sweep(triangle, centre, radius, half, delta,
            maximumError, ref work, scratch, admitted: true);
        if (!original.IsComplete || triangle.Geometry.SweepCompanion is not { } native ||
            !native.TryPose(triangle.Pose, out var map)) return default;
        InstalledSweepLeaf continuation = new(native, native.LeafPose, map);
        CapsuleSweepResult rounded = CapsuleConvexSweep.Sweep(continuation, centre, radius, half, delta,
            maximumError, ref work, scratch, admitted: true);
        if (!rounded.IsComplete || rounded.Status != original.Status) return default;
        if (original.Status == CapsuleSweepStatus.Clear) return original;
        if (original.ImpactDistance is not float a || rounded.ImpactDistance is not float b) return default;
        float lower = MathF.Min(original.ClearThroughDistance, rounded.ClearThroughDistance), upper = MathF.Max(a, b);
        double span = (double)upper - lower;
        float error = (float)span;
        if (error < span) error = MathF.BitIncrement(error);
        return error <= maximumError ? new(CapsuleSweepStatus.Hit, lower, upper, error) : default;
    }

    static bool FrontAxis(in InstalledSweepLeaf leaf, Vector3 centre, float half)
    {
        InstalledSweepGeometry geometry = leaf.Geometry;
        Vector3 a = geometry.Vertex(0), b = geometry.Vertex(1), c = geometry.Vertex(2);
        FeaturePoint origin = leaf.Map.Point(a, exactZeroProducts: true);
        FeaturePoint normal = FeaturePoint.Cross(leaf.Map.Edge(a, c), leaf.Map.Edge(a, b));
        FeaturePoint point = FeaturePoint.Exact(centre), extent = FeaturePoint.Exact(new Vector3(0, half, 0));
        FeaturePoint lower = FeaturePoint.Subtract(point, extent), upper = FeaturePoint.Add(point, extent);
        // With the complete axis on the front, closest edge/vertex normals also face front.
        // This is a strict subset of CapsuleTriangleTester.allowContacts and
        // allowFaceContacts, with no use or expansion of its backface rejection threshold.
        return InstalledMeshSides.Agree(leaf.Map, a, b, c, origin, normal, lower, upper,
            out GeometrySign low, out GeometrySign high) &&
            low is GeometrySign.Positive or GeometrySign.Zero && high is GeometrySign.Positive or GeometrySign.Zero;
    }

    static bool Direction(in InstalledSweepLeaf leaf, Vector3 delta, out bool culled)
    {
        culled = false;
        Vector3 a = leaf.Geometry.Vertex(0), b = leaf.Geometry.Vertex(1), c = leaf.Geometry.Vertex(2);
        FeaturePoint normal = FeaturePoint.Cross(leaf.Map.Edge(a, c), leaf.Map.Edge(a, b));
        GeometrySign sign = FeaturePoint.Dot(normal, FeaturePoint.Exact(delta)).Sign;
        if (sign == GeometrySign.Unresolved) return false;
        // HitHandlers.IsMeshBackFace uses this exact scalar graph, including scaling
        // already performed by Mesh.GetLocalChild at installation capture.
        Vector3 raw = Vector3.Transform(Vector3.Cross(c - a, b - a), leaf.Pose.Orientation);
        float value = Vector3.Dot(raw, delta);
        if (!float.IsFinite(value)) return false;
        bool source = value >= 0;
        culled = sign is GeometrySign.Positive or GeometrySign.Zero;
        return source == culled;
    }

    static bool Nondegenerate(InstalledSweepGeometry geometry)
    {
        Vector3 a = geometry.Vertex(0), b = geometry.Vertex(1), c = geometry.Vertex(2);
        FeaturePoint ab = FeaturePoint.Subtract(FeaturePoint.Exact(b), FeaturePoint.Exact(a));
        FeaturePoint ac = FeaturePoint.Subtract(FeaturePoint.Exact(c), FeaturePoint.Exact(a));
        FeaturePoint normal = FeaturePoint.Cross(ac, ab);
        // Match TriangleWide.ComputeNondegenerateTriangleMask's scale-dependent domain.
        // Relevant triangles outside it refuse, rather than becoming a silently empty leaf.
        FeatureNumber squared = FeaturePoint.Dot(normal, normal);
        FeatureNumber threshold = FeatureNumber.Exact(TriangleWide.DegenerateTriangleEpsilon);
        threshold = threshold.Multiply(threshold);
        if (squared.Compare(FeaturePoint.Dot(ab, ab).Multiply(threshold)) != GeometrySign.Positive ||
            squared.Compare(FeaturePoint.Dot(ac, ac).Multiply(threshold)) != GeometrySign.Positive) return false;
        GeometryVector rawA = FeaturePoint.Exact(a).Bounds;
        GeometryVector roundedAB = InstalledPoseOperations.Subtract(FeaturePoint.Exact(b).Bounds, rawA);
        GeometryVector roundedAC = InstalledPoseOperations.Subtract(FeaturePoint.Exact(c).Bounds, rawA);
        GeometryVector roundedNormal = InstalledPoseOperations.Cross(roundedAC, roundedAB);
        GeometryInterval normalLength = InstalledPoseOperations.Dot(roundedNormal, roundedNormal).Sqrt().EncloseSingleRounding();
        GeometryInterval abSquared = InstalledPoseOperations.Dot(roundedAB, roundedAB);
        GeometryInterval acSquared = InstalledPoseOperations.Dot(roundedAC, roundedAC);
        if (!normalLength.IsResolved || !abSquared.IsResolved || !acSquared.IsResolved) return false;
        GeometryInterval scale = GeometryInterval.Enclose(Math.Max(abSquared.Lower, acSquared.Lower),
            Math.Max(abSquared.Upper, acSquared.Upper)).Sqrt().EncloseSingleRounding();
        GeometryInterval epsilon = InstalledPoseOperations.Multiply(GeometryInterval.Exact(TriangleWide.DegenerateTriangleEpsilon), scale);
        return epsilon.IsResolved && normalLength.Lower > epsilon.Upper;
    }

    static CapsuleSweepResult Clear(Vector3 delta)
    {
        float length = (float)Math.Sqrt((double)delta.X * delta.X + (double)delta.Y * delta.Y + (double)delta.Z * delta.Z);
        return float.IsFinite(length) ? new(CapsuleSweepStatus.Clear, length, null, 0) : default;
    }
}
