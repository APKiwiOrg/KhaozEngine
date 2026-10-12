using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Complete raw tilted-part admission, attained roles and proposal certificates.
/// Every refusal is returned before the collector publishes either caller span.</summary>
internal sealed class SupportTiltedCurved
{
    readonly SupportTiltedCurvedGeometry _geometry = new();
    readonly SupportCurvedSurfaceAdmission _admission = new();
    internal void ResetStatistics() { _geometry.ResetStatistics(); _admission.ResetStatistics(); }

    internal GeometrySign Measure(StaticHandle owner, in SupportCurvedLeaf leaf, int part,
        in SupportFootprintRequest request, out SupportFootprintMember member)
    {
        member = default;
        if (!SupportFootprintBounds.TryColumn(request, out double bottom, out double top) ||
            !_geometry.Begin(leaf, request.Axis.X, request.Axis.Y, request.Radius)) return GeometrySign.Unresolved;
        GeometryInterval expanded = GeometryInterval.Exact(request.Radius).Add(GeometryInterval.Exact(request.ContactBand));
        int cap = leaf.Shape == SupportCurvedShape.Cylinder && part != SupportNeighborhoodCurved.Side ?
            _geometry.CapKind(part) : -1;
        GeometrySign admitted = _admission.Measure(_geometry, leaf.Shape, cap, request.Axis.X, request.Axis.Y,
            expanded, bottom, top);
        if (admitted != GeometrySign.Positive) return admitted;
        GeometrySign axis = Maximum(leaf, part, true, 0, out var axisPoint, out _);
        GeometrySign footprint = Maximum(leaf, part, false, 0, out var point, out var height);
        if (axis == GeometrySign.Unresolved || footprint == GeometrySign.Unresolved) return GeometrySign.Unresolved;
        if (footprint == GeometrySign.Positive && point.Normal.Y.Lower < request.CosMaxSlope)
        {
            GeometrySign walkable = Maximum(leaf, part, false, request.CosMaxSlope, out var replacement, out _);
            if (walkable == GeometrySign.Unresolved) return walkable;
            if (walkable == GeometrySign.Positive) point = replacement;
        }
        byte count = 0;
        SupportFootprintWitness first = default, second = default;
        if (axis == GeometrySign.Positive)
        {
            if (!SupportFootprintPublication.TryWitness(SupportWitnessRole.Axis, axisPoint.Position, axisPoint.Normal, out first))
                return GeometrySign.Unresolved;
            count++;
        }
        if (footprint == GeometrySign.Positive)
        {
            if (!SupportFootprintPublication.TryWitness(SupportWitnessRole.Footprint, point.Position, point.Normal, out var witness))
                return GeometrySign.Unresolved;
            if (count == 0) first = witness; else second = witness;
            count++;
        }
        member = new(owner, SupportElementKind.Tangent, leaf.LeafId * SupportNeighborhoodPolyhedra.FaceStride + part,
            default, 0, first.Normal, first.NormalError, first.UpwardLower, footprint == GeometrySign.Positive,
            footprint == GeometrySign.Positive ? height.Lower : 0, footprint == GeometrySign.Positive ? height.Upper : 0,
            0, count, first, second);
        return GeometrySign.Positive;
    }

    GeometrySign Maximum(in SupportCurvedLeaf leaf, int part, bool axis, double slope,
        out SupportTiltedCurvedGeometry.Witness witness, out GeometryInterval height)
    {
        if (leaf.Shape == SupportCurvedShape.Capsule) return _geometry.Round(axis, slope, out witness, out height);
        bool cap = part != SupportNeighborhoodCurved.Side;
        if (cap && _geometry.CapKind(part) == 3) { witness = default; height = default; return GeometrySign.Negative; }
        GeometrySign result = axis ? _geometry.Axis(cap, part, out witness) :
            cap ? _geometry.Cap(part, false, out witness, out _) : _geometry.Side(slope, out witness, out _);
        height = result == GeometrySign.Positive ? witness.Position.Y : default;
        if (cap && slope > 0 && result == GeometrySign.Positive) return _geometry.CapSlope(slope, ref witness);
        return result;
    }

    internal GeometrySign Lowest(in SupportCurvedLeaf leaf, int part, in SupportFootprintProposal proposal,
        double bandLower, double bandUpper, out GeometryInterval lowest)
    {
        lowest = default;
        if (!_geometry.Begin(leaf, proposal.Start.Position.X, proposal.Start.Position.Z, proposal.Probe.Radius))
            return GeometrySign.Unresolved;
        return _geometry.Contact(leaf.Shape, part, proposal.Probe.Radius, bandLower, bandUpper, out lowest);
    }
}
