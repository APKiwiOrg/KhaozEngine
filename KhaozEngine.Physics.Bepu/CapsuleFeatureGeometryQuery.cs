using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Selected finite minimum and atomic publication. All geometry, ordering, band and rounding
/// gates finish in private scratch before the caller's incident-face destination is touched.</summary>
internal static class CapsuleFeatureGeometryQuery
{
    internal static CapsuleFeatureResult Query(CapsuleFeaturePolyhedron[] leaves, IPhysicsWorld receiver,
        IPhysicsQueryLease lease, StaticHandle target, CapsuleShape capsule, Pose pose,
        float maximumSeparationMetres, Span<CapsuleIncidentFace> destination)
    {
        FeaturePoint center = FeaturePoint.Exact(pose.Position);
        FeatureNumber half = FeatureNumber.Exact(capsule.Length).Multiply(FeatureNumber.Exact(0.5));
        var lower = new FeaturePoint(center.X, center.Y.Subtract(half), center.Z);
        var upper = new FeaturePoint(center.X, center.Y.Add(half), center.Z);
        if (!lower.TrySingle(out _) || !upper.TrySingle(out _) || !InFrame(lower) || !InFrame(upper))
            return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.Unresolved);
        FeatureNumber radius = FeatureNumber.Exact(capsule.Radius);
        FeatureNumber band = FeatureNumber.Exact(maximumSeparationMetres);
        var closest = new CapsuleFeaturePolyhedraClosest(lower, upper, radius.Add(band));
        foreach (CapsuleFeaturePolyhedron leaf in leaves)
        {
            CapsuleFeatureStatus status = closest.Enumerate(leaf);
            if (status != CapsuleFeatureStatus.Complete) return CapsuleFeatureResult.Refused(status);
        }
        if (closest.Candidates.Count == 0) return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.NoFeature);
        CapsuleFeaturePolyhedronCandidate selected = closest.Candidates[0];
        bool ambiguous = false;
        for (int i = 1; i < closest.Candidates.Count; i++)
        {
            CapsuleFeaturePolyhedronCandidate candidate = closest.Candidates[i];
            GeometrySign order = FeaturePoint.CompareDistances(candidate.Axis, candidate.Geometry,
                selected.Axis, selected.Geometry);
            if (order == GeometrySign.Unresolved) order = candidate.SquaredDistance.Compare(selected.SquaredDistance);
            if (order == GeometrySign.Unresolved) return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.Unresolved);
            if (order == GeometrySign.Negative)
            {
                selected = candidate;
                ambiguous = false;
            }
            else if (order == GeometrySign.Zero)
            {
                bool same = ReferenceEquals(selected.Leaf, candidate.Leaf) && selected.FeatureId == candidate.FeatureId &&
                    selected.Axis.SameExact(candidate.Axis) && selected.Geometry.SameExact(candidate.Geometry);
                if (!same) ambiguous = true;
            }
        }
        if (ambiguous) return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.Ambiguous);

        // Certify the entire closed contact band. In particular a zero band needs exact equality,
        // not an interval overlapping zero. A deeper overlap is deliberately unresolved here.
        FeatureNumber minimumRadius = radius.Subtract(band);
        GeometrySign lowerBand = minimumRadius.IsExact
            ? FeaturePoint.CompareDistanceToRadius(selected.Axis, selected.Geometry, minimumRadius.Value)
            : GeometrySign.Unresolved;
        if (lowerBand == GeometrySign.Unresolved)
            lowerBand = selected.SquaredDistance.Compare(minimumRadius.Multiply(minimumRadius));
        if (lowerBand is not (GeometrySign.Zero or GeometrySign.Positive))
            return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.Unresolved);
        return Publish(selected, receiver, lease, target, radius, destination);
    }

    static CapsuleFeatureResult Publish(CapsuleFeaturePolyhedronCandidate selected, IPhysicsWorld receiver,
        IPhysicsQueryLease lease, StaticHandle target, FeatureNumber radius, Span<CapsuleIncidentFace> destination)
    {
        if (!InFrame(selected.Axis) || !InFrame(selected.Geometry))
            return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.Unsupported);
        GeometryVectorOutput axis = GeometryVectorOperations.Publish(selected.Axis.Bounds);
        GeometryVectorOutput geometry = GeometryVectorOperations.Publish(selected.Geometry.Bounds);
        GeometryVector direction = GeometryVectorOperations.Normalize(FeaturePoint.Subtract(selected.Axis, selected.Geometry).Bounds);
        GeometryVectorOutput normal = GeometryVectorOperations.Publish(direction);
        if (!axis.IsResolved || !geometry.IsResolved || !normal.IsResolved || !WithinCeiling(axis.Error, 4000) ||
            !WithinCeiling(geometry.Error, 4000) || !WithinCeiling(normal.Error, 100000))
            return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.Unresolved);
        GeometryInterval squared = selected.SquaredDistance.Bounds;
        if (!squared.IsResolved || squared.Lower <= 0)
            return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.Unresolved);
        GeometryInterval separation = squared.Sqrt().Subtract(radius.Bounds);
        GeometrySign exactContact = FeaturePoint.CompareDistanceToRadius(selected.Axis, selected.Geometry, radius.Value);
        if (exactContact == GeometrySign.Zero) separation = GeometryInterval.Exact(0);
        GeometryInterval width = GeometryInterval.Exact(separation.Upper).Subtract(GeometryInterval.Exact(separation.Lower));
        if (!separation.IsResolved || !width.IsResolved || !WithinCeiling(width.Upper, 10000))
            return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.Unresolved);
        int required = selected.IncidentFaces.Length;
        if (required is < 1 or > 256)
            return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.CapacityExceeded, required);
        var scratch = new CapsuleIncidentFace[required];
        for (int i = 0; i < required; i++)
        {
            int id = selected.IncidentFaces[i];
            GeometryVector faceNormal = GeometryVectorOperations.Normalize(selected.Leaf.Normals[id].Bounds);
            GeometryVectorOutput face = GeometryVectorOperations.Publish(faceNormal);
            if (!face.IsResolved || !WithinCeiling(face.Error, 100000))
                return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.Unresolved);
            scratch[i] = new(selected.Leaf.LeafId * CapsuleFeatureGeometry.MaximumFaces + id,
                face.Value, face.Error, selected.Kind);
        }
        if (destination.Length < required)
            return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.CapacityExceeded, required);
        // Construct first: lifecycle or structural failure must not expose a written prefix.
        CapsuleFeatureResult result = CapsuleFeatureResult.Completed(receiver, lease, target, selected.Leaf.LeafId,
            selected.FeatureId, selected.Kind, axis.Value, geometry.Value, normal.Value, separation.Lower, separation.Upper,
            Math.Max(axis.Error, geometry.Error), normal.Error, scratch);
        scratch.AsSpan().CopyTo(destination);
        return result;
    }

    static bool InFrame(FeaturePoint point) => point.IsResolved &&
        point.X.Bounds.Lower >= -2048 && point.X.Bounds.Upper <= 2048 &&
        point.Y.Bounds.Lower >= -2048 && point.Y.Bounds.Upper <= 2048 &&
        point.Z.Bounds.Lower >= -2048 && point.Z.Bounds.Upper <= 2048;

    static bool WithinCeiling(double value, double denominator) =>
        BoundedGeometryArithmetic.CompareProducts(value, denominator, 1, 1)
            is GeometrySign.Negative or GeometrySign.Zero;
}
