using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>One proved closest finite mesh relation, followed by complete witness-incidence and
/// the shared atomic output gates. No scratch prefix is observable on a refusal or exception.</summary>
internal static class CapsuleFeatureMeshQuery
{
    internal static CapsuleFeatureResult Query(CapsuleFeatureMesh mesh, IPhysicsWorld receiver,
        IPhysicsQueryLease lease, StaticHandle target, CapsuleShape capsule, Pose pose,
        float maximumSeparationMetres, Span<CapsuleIncidentFace> destination)
    {
        FeaturePoint center = FeaturePoint.Exact(pose.Position);
        FeatureNumber half = FeatureNumber.Exact(capsule.Length).Multiply(FeatureNumber.Exact(0.5));
        var lower = new FeaturePoint(center.X, center.Y.Subtract(half), center.Z);
        var upper = new FeaturePoint(center.X, center.Y.Add(half), center.Z);
        if (!lower.Within(2048) || !upper.Within(2048))
            return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.Unresolved);
        FeatureNumber radius = FeatureNumber.Exact(capsule.Radius);
        FeatureNumber band = FeatureNumber.Exact(maximumSeparationMetres);
        var closest = new CapsuleFeatureMeshClosest(mesh, lower, upper, radius.Add(band));
        CapsuleFeatureStatus enumerated = closest.Enumerate();
        if (enumerated != CapsuleFeatureStatus.Complete) return CapsuleFeatureResult.Refused(enumerated);
        if (closest.Candidates.Count == 0) return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.NoFeature);
        CapsuleFeatureMeshCandidate selected = closest.Candidates[0];
        bool ambiguous = false;
        for (int i = 1; i < closest.Candidates.Count; i++)
        {
            CapsuleFeatureMeshCandidate candidate = closest.Candidates[i];
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
                // Only one exact stratum and witness pair can alias. Coplanar edge/vertex strata
                // already represent their proved connected patch. Plane equality cannot join IDs.
                bool same = selected.FeatureId == candidate.FeatureId && selected.Axis.SameExact(candidate.Axis) &&
                    selected.Geometry.SameExact(candidate.Geometry);
                if (!same) ambiguous = true;
            }
        }
        if (ambiguous) return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.Ambiguous);
        // A larger vertex fan needs another embedding/cone proof unless its flat interior cycle
        // was proved. Its complete source link alone cannot certify the geometric neighborhood.
        if (selected.FeatureId >= mesh.Faces.Length + mesh.Edges.Length &&
            selected.IncidentFaces.Length > 2 && selected.Kind == CapsuleFeatureKind.Vertex)
            return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.Unresolved);
        CapsuleFeatureStatus incidence = mesh.ProveIncidence(selected.Geometry, selected.IncidentFaces);
        if (incidence != CapsuleFeatureStatus.Complete) return CapsuleFeatureResult.Refused(incidence);
        FeatureNumber minimumRadius = radius.Subtract(band);
        GeometrySign lowerBand = minimumRadius.IsExact
            ? FeaturePoint.CompareDistanceToRadius(selected.Axis, selected.Geometry, minimumRadius.Value)
            : GeometrySign.Unresolved;
        if (lowerBand == GeometrySign.Unresolved)
            lowerBand = selected.SquaredDistance.Compare(minimumRadius.Multiply(minimumRadius));
        if (lowerBand is not (GeometrySign.Zero or GeometrySign.Positive))
            return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.Unresolved);
        return CapsuleFeatureGeometryQuery.Publish(0, selected.FeatureId, selected.Kind, selected.Axis,
            selected.Geometry, selected.SquaredDistance, selected.IncidentFaces, mesh.Normals,
            CapsuleFeatureMesh.MaximumTriangles, receiver, lease, target, radius, destination);
    }
}
