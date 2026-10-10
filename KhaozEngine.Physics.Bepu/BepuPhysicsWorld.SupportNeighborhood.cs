using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld
{
    // Neighborhood scratch for this world. Every query on the world or one of its views runs under the world's
    // query monitor, so threads never share it. A query that finds it taken, which only a nested query on the
    // owning thread could, uses a fresh collector instead.
    SupportNeighborhoodCollector? _neighborhoodScratch = new();

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
        SupportNeighborhoodCollector collector = _neighborhoodScratch ?? new SupportNeighborhoodCollector();
        _neighborhoodScratch = null;
        try
        {
            return Collect(collector, receiver, lease, probe, pose, bandMetres, elements, joins, filter, exclusions);
        }
        finally
        {
            _neighborhoodScratch = collector;
        }
    }

    SupportNeighborhoodResult Collect(SupportNeighborhoodCollector collector, IPhysicsWorld receiver,
        IPhysicsQueryLease lease, CapsuleShape probe, Pose pose, float bandMetres, Span<SupportElement> elements,
        Span<ulong> joins, QueryFilter filter, StaticQueryExclusions? exclusions)
    {
        collector.Begin(probe, pose, bandMetres);
        if (!collector.IsResolved) return SupportNeighborhoodResult.Refused(CapsuleFeatureStatus.Unsupported);
        // A dynamics-only filter selects no static, so its neighborhood is certified empty.
        if (filter.Mobility == QueryMobility.Dynamics) return collector.Publish(receiver, lease, elements, joins);

        // Read the live registry and poses under the authenticated gate. Scratch is reused storage only: every
        // shape and pose is read again for each query, so no cached geometry outlives a shape removal or reuse.
        foreach (var (seam, installed) in collector.Candidates(_sim, _reverseHandles, exclusions, probe, pose,
                     bandMetres))
        {
            _sim.Statics.GetDescription(installed, out var description);
            CapsuleFeatureStatus collected = collector.Collect(_sim, seam, description);
            if (collected != CapsuleFeatureStatus.Complete) return SupportNeighborhoodResult.Refused(collected);
        }
        return collector.Publish(receiver, lease, elements, joins);
    }
}
