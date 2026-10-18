using KhaozEngine.Physics;
using Number = KhaozEngine.Physics.Bepu.SupportCurvedSourceArithmetic.Number;

namespace KhaozEngine.Physics.Bepu;

internal sealed partial class SupportFootprintCurved
{
    /// <summary>The first downward lower-sphere contact with this raw part. The sweep chooses the
    /// retained context. Its rounded hit distance never determines a proposal-band sign.</summary>
    internal GeometrySign Lowest(in SupportCurvedLeaf leaf, int part, in SupportFootprintProposal proposal,
        double bandLower, double bandUpper, out GeometryInterval lowest)
    {
        lowest = default;
        if (_work.Failed) return GeometrySign.Unresolved;
        if (!Begin(leaf, proposal.Start.Position.X, proposal.Start.Position.Z))
            return _tilted.Lowest(leaf, part, proposal, bandLower, bandUpper, out lowest);
        Number probe = C(proposal.Probe.Radius), y = _centre.Y, square;
        if (leaf.Shape != SupportCurvedShape.Cylinder)
        {
            Number distance = _distanceSquared;
            if (!_sphere && _upright) y = Add(y, _half);
            else if (!_sphere)
                distance = Add(Sq(_q), Sq(Sub(_p, CoreParameter())));
            square = Sub(Sq(Add(_radius, probe)), distance);
        }
        else if (_upright)
        {
            if (part != SupportNeighborhoodCurved.Side &&
                (part == SupportNeighborhoodCurved.TopCap ? _axisSign : -_axisSign) < 0)
                return GeometrySign.Negative;
            Number distance = Root(_distanceSquared);
            // The upward cap is filled. The side contacts the upper circular rim.
            Number gap = part == SupportNeighborhoodCurved.Side ? Abs(Sub(distance, _radius)) :
                Max(Sub(distance, _radius), C(0));
            square = Sub(Sq(probe), Sq(gap));
            y = Add(y, _half);
        }
        else
        {
            Number t = part == SupportNeighborhoodCurved.Side ? CoreParameter() :
                Mul(C(part == SupportNeighborhoodCurved.TopCap ? 1 : -1), _half);
            Number transverse = Sub(Sq(probe), Sq(Sub(_p, t)));
            GeometrySign possible = Sign(transverse);
            if (possible == GeometrySign.Negative || possible == GeometrySign.Unresolved) return possible;
            Number effective = Add(_radius, Root(transverse));
            square = Sub(Sq(effective), Sq(_q));
        }
        GeometrySign sign = Sign(square);
        if (sign == GeometrySign.Negative || sign == GeometrySign.Unresolved) return sign;
        Number height = Sub(Add(y, Root(square)), probe);
        // A source-backed equality certifies an authored band boundary exactly.
        if (Sign(Sub(height, C(bandLower))) == GeometrySign.Zero) height = C(bandLower);
        if (Sign(Sub(height, C(bandUpper))) == GeometrySign.Zero) height = C(bandUpper);
        lowest = Bounds(height);
        return lowest.IsResolved ? GeometrySign.Positive : GeometrySign.Unresolved;
    }
}
