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
        bool noUpperDomain = false;
        if (cap >= 0 && _geometry.CapNeedsUpperCoverage(part))
        {
            GeometryInterval reach = GeometryInterval.Exact(leaf.Radius).Add(GeometryInterval.Exact(leaf.HalfLength));
            GeometrySign upperDomain = _admission.Measure(_geometry, leaf.Shape, cap, request.Axis.X, request.Axis.Y,
                GeometryInterval.Exact(request.Radius), leaf.Centre.Y.Subtract(reach).Lower,
                leaf.Centre.Y.Add(reach).Upper, requireUpward: true);
            if (upperDomain == GeometrySign.Unresolved) return upperDomain;
            noUpperDomain = upperDomain == GeometrySign.Negative;
        }
        var axisPoint = default(SupportTiltedCurvedGeometry.Witness);
        var point = default(SupportTiltedCurvedGeometry.Witness);
        var height = default(GeometryInterval);
        GeometrySign axis = noUpperDomain ? GeometrySign.Negative : Maximum(leaf, part, true, 0, out axisPoint, out _);
        GeometrySign footprint = noUpperDomain ? GeometrySign.Negative : Maximum(leaf, part, false, 0, out point, out height);
        if (axis == GeometrySign.Unresolved || footprint == GeometrySign.Unresolved) return GeometrySign.Unresolved;
        // A cap has one normal, so its selected geometric point never needs replacement.
        // At cosine one, every walkable curved point attains its part's global height.
        if (footprint == GeometrySign.Positive && point.Normal.Y.Lower < request.CosMaxSlope)
        {
            if (cap >= 0) _geometry.RefineCapUpward(part, request.CosMaxSlope, ref point);
            else if (request.CosMaxSlope < 1 && !point.AtSideCrest)
            {
                GeometrySign walkable = Maximum(leaf, part, false, request.CosMaxSlope, out var replacement, out _);
                if (walkable == GeometrySign.Unresolved) return walkable;
                if (walkable == GeometrySign.Positive) point = replacement;
            }
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
        if (cap && _geometry.CapFacesDown(part)) { witness = default; height = default; return GeometrySign.Negative; }
        GeometrySign result = axis ? _geometry.Axis(cap, part, out witness) :
            cap ? _geometry.Cap(part, false, out witness, out _) : _geometry.Side(slope, out witness, out _);
        height = result == GeometrySign.Positive ? witness.Position.Y : default;
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
