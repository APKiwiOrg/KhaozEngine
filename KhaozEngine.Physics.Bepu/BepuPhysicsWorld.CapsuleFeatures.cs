using System;
using System.Numerics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld : IPhysicsCapsuleFeatures
{
    /// <inheritdoc/>
    public CapsuleFeatureResult QueryCapsuleFeature(IPhysicsQueryLease lease, StaticHandle target,
        CapsuleShape capsule, Pose pose, float maximumSeparationMetres, Span<CapsuleIncidentFace> faces,
        QueryFilter filter = default) =>
        QueryCapsuleFeatureCore(this, lease, target, capsule, pose, maximumSeparationMetres, faces, filter, null);

    /// <inheritdoc/>
    public void AssertFeatureCurrent(in CapsuleFeatureResult result, IPhysicsQueryLease lease) =>
        AssertFeatureCurrentCore(this, result, lease);

    void AuthenticateFeatureLease(IPhysicsQueryLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        // Do not invoke a user-supplied AssertCurrent or enter the monitor before proving ownership.
        if (lease is not ReadLease actual || !ReferenceEquals(actual.SourceWorld, this))
            throw new ArgumentException("The lease was not issued by this physics owner.", nameof(lease));
        actual.AssertCurrent(); // Includes the thread check before any monitor acquisition.
    }

    void AssertFeatureCurrentCore(IPhysicsWorld receiver, in CapsuleFeatureResult result, IPhysicsQueryLease lease)
    {
        if (result.Status != CapsuleFeatureStatus.Complete || !ReferenceEquals(result.QueryWorld, receiver) ||
            !ReferenceEquals(result.Lease, lease))
            throw new InvalidOperationException("The feature belongs to another receiver or read interval.");
        AuthenticateFeatureLease(lease);
        if (!ReferenceEquals(result.SourceWorld, this) || result.Origin != lease.Origin ||
            result.GeometryGeneration != lease.GeometryGeneration)
            throw new InvalidOperationException("The feature metadata does not describe its original read interval.");
    }

    CapsuleFeatureResult QueryCapsuleFeatureCore(IPhysicsWorld receiver, IPhysicsQueryLease lease, StaticHandle target,
        CapsuleShape capsule, Pose pose, float maximumSeparationMetres, Span<CapsuleIncidentFace> faces,
        QueryFilter filter, StaticQueryExclusions? exclusions)
    {
        AuthenticateFeatureLease(lease);
        using QueryOperation scope = EnterQuery();
        ArgumentNullException.ThrowIfNull(capsule);
        if (!FeatureFinite(pose.Position) || !float.IsFinite(pose.Orientation.X) ||
            !float.IsFinite(pose.Orientation.Y) || !float.IsFinite(pose.Orientation.Z) ||
            !float.IsFinite(pose.Orientation.W) || pose.Orientation == default)
            throw new ArgumentException("The feature pose must have finite coordinates and a nonzero rotation.", nameof(pose));
        if (!float.IsFinite(capsule.Radius) || capsule.Radius <= 0 ||
            !float.IsFinite(capsule.Length) || capsule.Length < 0)
            throw new ArgumentException("The capsule must have a positive finite radius and nonnegative finite length.", nameof(capsule));
        if (!float.IsFinite(maximumSeparationMetres) || maximumSeparationMetres < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumSeparationMetres));
        if (filter.Mobility is < QueryMobility.All or > QueryMobility.Dynamics || filter.Layers != 0)
            throw new ArgumentException("The feature query does not support this filter value.", nameof(filter));
        if (!_handles.TryGetValue(target.Value, out var entry) || filter.Mobility == QueryMobility.Dynamics ||
            (exclusions is not null && !exclusions.Allows(new CollidableReference(entry.Handle))))
            return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.Unavailable);
        if (pose.Orientation.X != 0f || pose.Orientation.Z != 0f ||
            capsule.Radius is < 0.01f or > 2f || capsule.Length > 8f || maximumSeparationMetres > 0.01f ||
            !RepresentedGeometryTransforms.PosePoint(pose, Vector3.Zero).IsResolved)
            return CapsuleFeatureResult.Refused(CapsuleFeatureStatus.Unsupported);

        // Read the live registry and pose under the authenticated gate, including rebase changes.
        // Uncached managed scratch avoids shape removal/reuse and cache lifetime mutation hooks.
        _sim.Statics.GetDescription(entry.Handle, out var description);
        if (description.Shape.Type == default(Mesh).TypeId)
        {
            CapsuleFeatureStatus meshCaptured = CapsuleFeatureMesh.Capture(_sim, description.Shape, description.Pose,
                out CapsuleFeatureMesh? mesh);
            if (meshCaptured != CapsuleFeatureStatus.Complete) return CapsuleFeatureResult.Refused(meshCaptured);
            return CapsuleFeatureMeshQuery.Query(mesh!, receiver, lease, target, capsule, pose,
                maximumSeparationMetres, faces);
        }
        CapsuleFeatureStatus captured = CapsuleFeatureGeometry.Capture(_sim, description.Shape, description.Pose,
            out CapsuleFeaturePolyhedron[] leaves);
        if (captured != CapsuleFeatureStatus.Complete) return CapsuleFeatureResult.Refused(captured);
        return CapsuleFeatureGeometryQuery.Query(leaves, receiver, lease, target, capsule, pose,
            maximumSeparationMetres, faces);
    }

    static bool FeatureFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
