using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;
using BepuStaticHandle = BepuPhysics.StaticHandle;
using StaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Physics.Bepu;

/// <summary>One complete column and the two retained contact contexts, collected into bounded heap storage.
/// Every raw pair is captured once. Joins and lease metadata finish before either caller span is touched.</summary>
internal sealed class SupportFootprintCollector
{
    readonly record struct Member(SupportPolygon? Polygon, SupportCurvedLeaf Curved, int Part, SupportFootprintMember Value);
    readonly Member[] _members = new Member[SupportFootprintResult.MaximumElements];
    readonly ulong[] _matrix = new ulong[1024];
    readonly SupportPolygonPool _pool = new();
    readonly CapsuleFeatureCaptureScratch _capture = new();
    readonly SupportSegmentPolygon _kernel = new();
    readonly List<CapsuleFeaturePolyhedron> _leaves = [];
    readonly List<SupportCurvedLeaf> _curved = [];
    readonly SupportFootprintCurved _curvedKernel = new();
    readonly SupportTangentKernel _contactKernel = new();
    readonly List<SupportPolygon> _polygons = [];
    readonly List<int> _triangles = [];
    readonly List<CollidableReference> _found = [];
    readonly List<(StaticHandle Seam, BepuStaticHandle Installed)> _candidates = [];
    readonly HashSet<int> _seen = [];
    readonly SupportProposalVerdict[] _verdicts = new SupportProposalVerdict[2];
    readonly Pose[] _contacts = new Pose[2];
    readonly StaticHandle?[] _targets = new StaticHandle?[2];
    readonly double[] _contactLow = new double[2], _contactHigh = new double[2];
    readonly bool[] _hasContactGeometry = new bool[2];
    SupportFootprintRequest _request;
    SupportFootprintBounds _bounds;
    int _count;

    internal bool Begin(in SupportFootprintRequest request)
    {
        _request = request;
        _count = 0;
        _curvedKernel.ResetStatistics();
        _pool.Reset();
        _capture.Reset();
        Array.Clear(_verdicts);
        Array.Clear(_targets);
        Array.Clear(_hasContactGeometry);
        return SupportFootprintBounds.TryCreate(request, out _bounds);
    }

    internal void Proposal(int index, bool hit, in SweepHit contact)
    {
        if (!hit) return;
        SupportFootprintProposal proposal = index == 0 ? _request.AxisProposal : _request.FootProposal;
        GeometryInterval startLowest = GeometryInterval.Exact(proposal.Start.Position.Y)
            .Subtract(GeometryInterval.Exact(proposal.Probe.Length).Multiply(GeometryInterval.Exact(0.5)))
            .Subtract(GeometryInterval.Exact(proposal.Probe.Radius));
        _targets[index] = contact.Body;
        if (contact.Distance == 0 && contact.Normal == Vector3.Zero)
        {
            _verdicts[index] = new(SupportProposalStatus.InvalidStart, startLowest.Lower, startLowest.Upper);
            return;
        }
        _verdicts[index] = new(SupportProposalStatus.Contact, 0, 0);
        _contacts[index] = Pose.At(new(proposal.Start.Position.X, proposal.Start.Position.Y - contact.Distance,
            proposal.Start.Position.Z));
    }

    internal List<(StaticHandle Seam, BepuStaticHandle Installed)> Candidates(Simulation simulation,
        IReadOnlyDictionary<int, int> reverse, StaticQueryExclusions? exclusions)
    {
        SupportQueryDiagnostics.Record(SupportQueryCounter.Volumes);
        _found.Clear();
        _candidates.Clear();
        _seen.Clear();
        var overlaps = new OverlapCollector(_found);
        simulation.BroadPhase.GetOverlaps(_bounds.Minimum, _bounds.Maximum, ref overlaps);
        foreach (CollidableReference item in _found)
        {
            if (item.Mobility != CollidableMobility.Static || (exclusions is not null && !exclusions.Allows(item)) ||
                !reverse.TryGetValue(item.StaticHandle.Value, out int id) || !_seen.Add(id)) continue;
            _candidates.Add((new StaticHandle(id), item.StaticHandle));
        }
        _candidates.Sort(static (a, b) => a.Seam.Value.CompareTo(b.Seam.Value));
        SupportQueryDiagnostics.Record(SupportQueryCounter.SelectedStatics, _candidates.Count);
        return _candidates;
    }

    internal CapsuleFeatureStatus Collect(Simulation simulation, StaticHandle owner, in StaticDescription description)
    {
        _polygons.Clear();
        _curved.Clear();
        CapsuleFeatureStatus captured;
        if (SupportNeighborhoodMesh.IsMesh(description.Shape))
            captured = SupportNeighborhoodMesh.Polygons(simulation, description.Shape, description.Pose, owner,
                _bounds, _triangles, _pool, _polygons);
        else
        {
            captured = SupportNeighborhoodPolyhedra.Capture(simulation, description.Shape, description.Pose,
                _bounds, _capture, _leaves, _curved);
            if (captured != CapsuleFeatureStatus.Complete) return captured;
            foreach (CapsuleFeaturePolyhedron leaf in _leaves)
                SupportNeighborhoodPolyhedra.Polygons(owner, leaf, _pool, _polygons, footprint: true);
        }
        if (captured != CapsuleFeatureStatus.Complete) return captured;
        foreach (SupportPolygon polygon in _polygons)
        {
            GeometrySign admitted = SupportFootprintPolygon.Measure(polygon, _request, out SupportFootprintMember value);
            if (admitted == GeometrySign.Unresolved) return CapsuleFeatureStatus.Unsupported;
            if (admitted == GeometrySign.Negative) continue;
            byte mask = 0;
            for (int proposal = 0; proposal < 2; proposal++)
            {
                if (_verdicts[proposal].Status != SupportProposalStatus.Contact) continue;
                GeometrySign member = ContactMember(polygon, proposal);
                if (member == GeometrySign.Unresolved) return CapsuleFeatureStatus.Unsupported;
                if (member != GeometrySign.Positive) continue;
                mask |= (byte)(1 << proposal);
                if (!ContactHeight(polygon, proposal)) return CapsuleFeatureStatus.Unsupported;
            }
            SupportQueryDiagnostics.Record(SupportQueryCounter.Members);
            if (++_count > SupportFootprintResult.MaximumElements) return CapsuleFeatureStatus.CapacityExceeded;
            _members[_count - 1] = new(polygon, default, 0, value with { ProposalMask = mask });
        }
        foreach (SupportCurvedLeaf leaf in _curved)
            for (int part = 0; part < leaf.Parts; part++)
            {
                GeometrySign admitted = _curvedKernel.Measure(owner, leaf, part, _request, out SupportFootprintMember value);
                if (admitted == GeometrySign.Unresolved) return CapsuleFeatureStatus.Unsupported;
                if (admitted == GeometrySign.Negative) continue;
                byte mask = 0;
                for (int proposal = 0; proposal < 2; proposal++)
                {
                    if (_verdicts[proposal].Status != SupportProposalStatus.Contact) continue;
                    GeometrySign context = CurvedContactMember(owner, leaf, part, proposal);
                    if (context == GeometrySign.Unresolved) return CapsuleFeatureStatus.Unsupported;
                    if (context != GeometrySign.Positive) continue;
                    mask |= (byte)(1 << proposal);
                    if (!CurvedContactHeight(leaf, part, proposal)) return CapsuleFeatureStatus.Unsupported;
                }
                SupportQueryDiagnostics.Record(SupportQueryCounter.Members);
                if (++_count > SupportFootprintResult.MaximumElements) return CapsuleFeatureStatus.CapacityExceeded;
                _members[_count - 1] = new(null, leaf, part, value with { ProposalMask = mask });
            }
        return CapsuleFeatureStatus.Complete;
    }

    GeometrySign ContactMember(SupportPolygon polygon, int index)
    {
        SupportFootprintProposal proposal = index == 0 ? _request.AxisProposal : _request.FootProposal;
        FeaturePoint centre = FeaturePoint.Exact(_contacts[index].Position);
        FeatureNumber half = FeatureNumber.Exact(proposal.Probe.Length).Multiply(FeatureNumber.Exact(0.5));
        var start = new FeaturePoint(centre.X, centre.Y.Subtract(half), centre.Z);
        var end = new FeaturePoint(centre.X, centre.Y.Add(half), centre.Z);
        SupportDistance distance = _kernel.Measure(start, FeaturePoint.Subtract(end, start), polygon, _request.ContactBand);
        if (!distance.IsResolved) return GeometrySign.Unresolved;
        GeometryInterval separation = GeometryInterval.Enclose(distance.Lower, distance.Upper)
            .Subtract(GeometryInterval.Exact(proposal.Probe.Radius));
        if (!separation.IsResolved) return GeometrySign.Unresolved;
        return separation.Lower <= _request.ContactBand && distance.Front ? GeometrySign.Positive : GeometrySign.Negative;
    }

    bool ContactHeight(SupportPolygon polygon, int index, bool sourceHeight = false)
    {
        SupportFootprintProposal proposal = index == 0 ? _request.AxisProposal : _request.FootProposal;
        GeometrySign height = SupportFootprintProposalGeometry.Lowest(polygon, proposal, out GeometryInterval bounds, sourceHeight);
        if (height == GeometrySign.Unresolved) return false;
        if (height != GeometrySign.Positive) return true;
        _contactLow[index] = _hasContactGeometry[index] ? Math.Max(_contactLow[index], bounds.Lower) : bounds.Lower;
        _contactHigh[index] = _hasContactGeometry[index] ? Math.Max(_contactHigh[index], bounds.Upper) : bounds.Upper;
        _hasContactGeometry[index] = true;
        return true;
    }

    GeometrySign CurvedContactMember(StaticHandle owner, in SupportCurvedLeaf leaf, int part, int index)
    {
        SupportFootprintProposal proposal = index == 0 ? _request.AxisProposal : _request.FootProposal;
        FeaturePoint centre = FeaturePoint.Exact(_contacts[index].Position);
        FeatureNumber half = FeatureNumber.Exact(proposal.Probe.Length).Multiply(FeatureNumber.Exact(0.5));
        _contactKernel.Reset(new(centre.X, centre.Y.Subtract(half), centre.Z),
            new(centre.X, centre.Y.Add(half), centre.Z), GeometryInterval.Exact(proposal.Probe.Radius), _request.ContactBand);
        return _contactKernel.Measure(owner, leaf, part, out _);
    }

    bool CurvedContactHeight(in SupportCurvedLeaf leaf, int part, int index)
    {
        SupportFootprintProposal proposal = index == 0 ? _request.AxisProposal : _request.FootProposal;
        GeometrySign height = _curvedKernel.Lowest(leaf, part, proposal, _request.BandLower, _request.BandUpper, out var bounds);
        if (height == GeometrySign.Unresolved) return false;
        if (height != GeometrySign.Positive) return true;
        _contactLow[index] = _hasContactGeometry[index] ? Math.Max(_contactLow[index], bounds.Lower) : bounds.Lower;
        _contactHigh[index] = _hasContactGeometry[index] ? Math.Max(_contactHigh[index], bounds.Upper) : bounds.Upper;
        _hasContactGeometry[index] = true;
        return true;
    }

    bool RefineContact(in Member member, int index) => member.Polygon is { } polygon ?
        ContactHeight(polygon, index, sourceHeight: true) : CurvedContactHeight(member.Curved, member.Part, index);

    internal SupportFootprintResult Refused(CapsuleFeatureStatus status) =>
        SupportFootprintResult.Refused(status, status == CapsuleFeatureStatus.CapacityExceeded ? _count : 0);

    internal SupportFootprintResult Publish(IPhysicsWorld receiver, IPhysicsQueryLease lease,
        Span<SupportFootprintMember> members, Span<ulong> joins)
    {
        int stride = SupportFootprintResult.JoinWordsFor(_count);
        if (_count > SupportFootprintResult.MaximumElements || members.Length < _count || joins.Length < _count * stride)
            return Refused(CapsuleFeatureStatus.CapacityExceeded);
        for (int index = 0; index < 2; index++)
        {
            if (_verdicts[index].Status != SupportProposalStatus.Contact) continue;
            if (_targets[index] is null || !_hasContactGeometry[index]) return Refused(CapsuleFeatureStatus.Unsupported);
            double low = _contactLow[index], high = _contactHigh[index];
            if (BandUncertain(low, high))
            {
                _hasContactGeometry[index] = false;
                for (int member = 0; member < _count; member++)
                    if ((_members[member].Value.ProposalMask & (1 << index)) != 0 &&
                        !RefineContact(_members[member], index))
                        return Refused(CapsuleFeatureStatus.Unsupported);
                if (!_hasContactGeometry[index]) return Refused(CapsuleFeatureStatus.Unsupported);
                low = _contactLow[index]; high = _contactHigh[index];
            }
            // An uncertain side of either inclusive band boundary is a refusal, never a rounded sweep sign.
            if (BandUncertain(low, high)) return Refused(CapsuleFeatureStatus.Unsupported);
            _verdicts[index] = new(SupportProposalStatus.Contact, low, high);
        }
        _members.AsSpan(0, _count).Sort(static (a, b) => a.Value.Static != b.Value.Static
            ? a.Value.Static.Value.CompareTo(b.Value.Static.Value) : a.Value.ElementId.CompareTo(b.Value.ElementId));
        Span<ulong> matrix = _matrix.AsSpan(0, _count * stride);
        matrix.Clear();
        for (int first = 0; first < _count; first++)
            for (int second = first + 1; second < _count; second++)
            {
                SupportQueryDiagnostics.Record(SupportQueryCounter.JoinPairs);
                if (_members[first].Polygon is not { } a || _members[second].Polygon is not { } b) continue;
                if (SupportGeometry.BoxGap(a.Low, a.High, b.Low, b.High) > _request.ContactBand)
                {
                    SupportQueryDiagnostics.Record(SupportQueryCounter.JoinsPruned);
                    continue;
                }
                GeometrySign joined = SupportNeighborhoodJoins.Joined(a, b, _request.ContactBand, _kernel);
                if (joined == GeometrySign.Unresolved) return Refused(CapsuleFeatureStatus.Unsupported);
                if (joined != GeometrySign.Positive) continue;
                SupportQueryDiagnostics.Record(SupportQueryCounter.JoinsProved);
                matrix[first * stride + second / 64] |= 1UL << (second % 64);
                matrix[second * stride + first / 64] |= 1UL << (first % 64);
            }
        int witnesses = 0;
        for (int i = 0; i < _count; i++) witnesses += _members[i].Value.WitnessCount;
        SupportFootprintResult result = SupportFootprintResult.Completed(receiver, lease, _count, witnesses,
            _verdicts[0], _verdicts[1]);
        for (int i = 0; i < _count; i++) members[i] = _members[i].Value;
        matrix.CopyTo(joins);
        return result;
    }

    bool BandUncertain(double low, double high) =>
        (low < _request.BandLower && high >= _request.BandLower) ||
        (low <= _request.BandUpper && high > _request.BandUpper);
}
