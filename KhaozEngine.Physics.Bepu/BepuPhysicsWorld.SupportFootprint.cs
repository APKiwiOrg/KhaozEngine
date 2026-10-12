using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld : IPhysicsSupportFootprint
{
    SupportFootprintCollector? _footprintScratch = new();
    readonly InstalledSupportGeometryCache _installedSupportGeometry = new();
    long _staticGeometryVersion;

    // The mutation gate protects this version independently of moving bodies and query generations.
    void StaticGeometryChanged(StaticHandle? owner = null)
    {
        checked { _staticGeometryVersion++; }
        if (owner is { } changed) _installedSupportGeometry.Remove(changed);
        else _installedSupportGeometry.Clear();
    }

    /// <inheritdoc/>
    public SupportFootprintResult QuerySupportFootprint(IPhysicsQueryLease lease, in SupportFootprintRequest request,
        Span<SupportFootprintMember> members, Span<ulong> joins, QueryFilter filter = default) =>
        QuerySupportFootprintCore(this, lease, request, members, joins, filter, null);

    /// <inheritdoc/>
    public void AssertFootprintCurrent(in SupportFootprintResult result, IPhysicsQueryLease lease) =>
        AssertFootprintCurrentCore(this, result, lease);

    void AssertFootprintCurrentCore(IPhysicsWorld receiver, in SupportFootprintResult result, IPhysicsQueryLease lease)
    {
        AuthenticateFeatureLease(lease);
        if (result.Status != CapsuleFeatureStatus.Complete || !ReferenceEquals(result.QueryWorld, receiver) ||
            !ReferenceEquals(result.Lease, lease))
            throw new InvalidOperationException("The footprint belongs to another receiver or read interval.");
        if (!ReferenceEquals(result.SourceWorld, this) || result.Origin != lease.Origin ||
            result.GeometryGeneration != lease.GeometryGeneration)
            throw new InvalidOperationException("The footprint metadata does not describe its original read interval.");
    }

    SupportFootprintResult QuerySupportFootprintCore(IPhysicsWorld receiver, IPhysicsQueryLease lease,
        in SupportFootprintRequest request, Span<SupportFootprintMember> members, Span<ulong> joins,
        QueryFilter filter, StaticQueryExclusions? exclusions)
    {
        AuthenticateFeatureLease(lease);
        using QueryOperation scope = EnterQuery();
        ArgumentNullException.ThrowIfNull(request.AxisProposal.Probe);
        ArgumentNullException.ThrowIfNull(request.FootProposal.Probe);
        if (filter.Mobility is < QueryMobility.All or > QueryMobility.Dynamics || filter.Layers != 0)
            throw new ArgumentException("The footprint query does not support this filter value.", nameof(filter));
        if (joins.Length < (long)members.Length * SupportFootprintResult.JoinWordsFor(members.Length))
            throw new ArgumentException("The join span must hold members.Length * JoinWordsFor(members.Length) words.", nameof(joins));
        SupportFootprintCollector collector = _footprintScratch ?? new SupportFootprintCollector();
        _footprintScratch = null;
        try
        {
            SupportFootprintResult result = CollectFootprint(collector, receiver, lease, request, members, joins, filter, exclusions);
            SupportQueryDiagnostics.Refusal(result.Status);
            return result;
        }
        finally { _footprintScratch = collector; }
    }

    SupportFootprintResult CollectFootprint(SupportFootprintCollector collector, IPhysicsWorld receiver,
        IPhysicsQueryLease lease, in SupportFootprintRequest request, Span<SupportFootprintMember> members,
        Span<ulong> joins, QueryFilter filter, StaticQueryExclusions? exclusions)
    {
        if (!collector.Begin(request) || request.Radius > 2 || !FootprintProposalDomain(request.AxisProposal) ||
            !FootprintProposalDomain(request.FootProposal)) return collector.Refused(CapsuleFeatureStatus.Unsupported);
        QueryFilter proposals = QueryFilter.StaticsOnly with { CullBackFaces = true };
        for (int index = 0; index < 2; index++)
        {
            using var phase = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.SupportProposals);
            SupportQueryDiagnostics.Record(SupportQueryCounter.Proposals);
            SupportFootprintProposal proposal = index == 0 ? request.AxisProposal : request.FootProposal;
            SweepHit hit = default;
            bool found = filter.Mobility != QueryMobility.Dynamics && SweepCapsuleCore(proposal.Probe, proposal.Start,
                -Vector3.UnitY, proposal.Distance, out hit, proposals, exclusions);
            collector.Proposal(index, found, hit);
        }
        // Selection and live pose proofs run under the original lease. Only admitted local topology is retained.
        System.Collections.Generic.List<(StaticHandle Seam, BepuPhysics.StaticHandle Installed)> candidates;
        using (ContactAllocationDiagnostics.Begin(ContactAllocationPhase.SupportTraversal))
            candidates = collector.Candidates(_sim, _reverseHandles, exclusions);
        foreach (var (seam, installed) in candidates)
        {
            if (filter.Mobility == QueryMobility.Dynamics) break;
            _sim.Statics.GetDescription(installed, out var description);
            CapsuleFeatureStatus captured = collector.Collect(_sim, seam, description, _installedSupportGeometry,
                _staticGeometryVersion);
            if (captured != CapsuleFeatureStatus.Complete) return collector.Refused(captured);
        }
        if (filter.Mobility != QueryMobility.Dynamics && collector.BeginBelowBandPass())
            foreach (var (seam, installed) in candidates)
            {
                _sim.Statics.GetDescription(installed, out var description);
                CapsuleFeatureStatus captured = collector.Collect(_sim, seam, description, _installedSupportGeometry,
                    _staticGeometryVersion);
                if (captured != CapsuleFeatureStatus.Complete) return collector.Refused(captured);
            }
        return collector.Publish(receiver, lease, members, joins);
    }

    static bool FootprintProposalDomain(in SupportFootprintProposal proposal) =>
        proposal.Probe.Radius is >= 0.01f and <= 2f && proposal.Probe.Length <= 8 &&
        RepresentedGeometryTransforms.PosePoint(proposal.Start, Vector3.Zero).IsResolved;
}
