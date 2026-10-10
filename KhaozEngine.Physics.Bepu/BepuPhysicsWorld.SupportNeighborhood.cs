using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld
{
    /// <inheritdoc/>
    public SupportNeighborhoodResult QuerySupportNeighborhood(IPhysicsQueryLease lease, CapsuleShape probe, Pose pose,
        float bandMetres, Span<SupportElement> elements, Span<ulong> joins, QueryFilter filter = default) =>
        QuerySupportNeighborhoodCore(this, lease, probe, pose, bandMetres, elements, joins, filter, null);

    /// <inheritdoc/>
    public void AssertNeighborhoodCurrent(in SupportNeighborhoodResult result, IPhysicsQueryLease lease) =>
        AssertNeighborhoodCurrentCore(this, result, lease);

    void AssertNeighborhoodCurrentCore(IPhysicsWorld receiver, in SupportNeighborhoodResult result,
        IPhysicsQueryLease lease)
    {
        if (result.Status != CapsuleFeatureStatus.Complete || !ReferenceEquals(result.QueryWorld, receiver) ||
            !ReferenceEquals(result.Lease, lease))
            throw new InvalidOperationException("The neighborhood belongs to another receiver or read interval.");
        AuthenticateFeatureLease(lease);
        if (!ReferenceEquals(result.SourceWorld, this) || result.Origin != lease.Origin ||
            result.GeometryGeneration != lease.GeometryGeneration)
            throw new InvalidOperationException("The neighborhood metadata does not describe its original read interval.");
    }

    SupportNeighborhoodResult QuerySupportNeighborhoodCore(IPhysicsWorld receiver, IPhysicsQueryLease lease,
        CapsuleShape probe, Pose pose, float bandMetres, Span<SupportElement> elements, Span<ulong> joins,
        QueryFilter filter, StaticQueryExclusions? exclusions)
    {
        AuthenticateFeatureLease(lease);
        using QueryOperation scope = EnterQuery();
        ArgumentNullException.ThrowIfNull(probe);
        if (!FeatureFinite(pose.Position) || !float.IsFinite(pose.Orientation.X) ||
            !float.IsFinite(pose.Orientation.Y) || !float.IsFinite(pose.Orientation.Z) ||
            !float.IsFinite(pose.Orientation.W) || pose.Orientation == default)
            throw new ArgumentException("The probe pose must have finite coordinates and a nonzero rotation.", nameof(pose));
        if (!float.IsFinite(probe.Radius) || probe.Radius <= 0 || !float.IsFinite(probe.Length) || probe.Length < 0)
            throw new ArgumentException("The probe must have a positive finite radius and nonnegative finite length.", nameof(probe));
        if (!float.IsFinite(bandMetres) || bandMetres < 0)
            throw new ArgumentOutOfRangeException(nameof(bandMetres));
        if (filter.Mobility is < QueryMobility.All or > QueryMobility.Dynamics || filter.Layers != 0)
            throw new ArgumentException("The neighborhood query does not support this filter value.", nameof(filter));
        // The same proven domain as the feature query: an upright probe of bounded size and a bounded band.
        if (pose.Orientation.X != 0f || pose.Orientation.Z != 0f ||
            probe.Radius is < 0.01f or > 2f || probe.Length > 8f || bandMetres > 0.01f ||
            !RepresentedGeometryTransforms.PosePoint(pose, Vector3.Zero).IsResolved)
            return SupportNeighborhoodResult.Refused(CapsuleFeatureStatus.Unsupported);
        var collector = new SupportNeighborhoodCollector(probe, pose, bandMetres);
        if (!collector.IsResolved) return SupportNeighborhoodResult.Refused(CapsuleFeatureStatus.Unsupported);
        // A dynamics-only filter selects no static, so its neighborhood is certified empty.
        if (filter.Mobility == QueryMobility.Dynamics) return collector.Publish(receiver, lease, elements, joins);

        // Read the live registry and poses under the authenticated gate. Uncached managed scratch avoids shape
        // removal or reuse and cache lifetime hooks, as the feature query does.
        foreach (var (seam, installed) in SupportNeighborhoodCollector.Candidates(_sim, _reverseHandles, exclusions,
                     probe, pose, bandMetres))
        {
            _sim.Statics.GetDescription(installed, out var description);
            CapsuleFeatureStatus collected = collector.Collect(_sim, seam, description);
            if (collected != CapsuleFeatureStatus.Complete) return SupportNeighborhoodResult.Refused(collected);
        }
        return collector.Publish(receiver, lease, elements, joins);
    }
}
