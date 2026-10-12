using KhaozEngine.Physics;
using Number = KhaozEngine.Physics.Bepu.SupportCurvedSourceArithmetic.Number;

namespace KhaozEngine.Physics.Bepu;

internal sealed partial class SupportFootprintCurved
{
    GeometrySign Overlap(Number low, Number high, Number bottom, Number top)
    {
        GeometrySign above = Sign(Sub(low, top)), below = Sign(Sub(high, bottom));
        if (above == GeometrySign.Positive || below == GeometrySign.Negative) return GeometrySign.Negative;
        return above == GeometrySign.Unresolved || below == GeometrySign.Unresolved ? GeometrySign.Unresolved : GeometrySign.Positive;
    }

    GeometrySign Admission(in SupportCurvedLeaf leaf, int part, Number disc, Number bottom, Number top)
    {
        if (leaf.Shape != SupportCurvedShape.Cylinder) return RoundAdmission(disc, bottom, top);
        if (_upright)
        {
            Number distance = Root(_distanceSquared);
            GeometrySign outer = Sign(Sub(distance, Add(disc, _radius)));
            if (outer == GeometrySign.Positive || outer == GeometrySign.Unresolved) return outer == GeometrySign.Positive ? GeometrySign.Negative : outer;
            if (part == SupportNeighborhoodCurved.Side)
            {
                GeometrySign inner = Sign(Sub(Add(distance, disc), _radius));
                if (inner == GeometrySign.Negative || inner == GeometrySign.Unresolved) return inner;
                return Overlap(Sub(_centre.Y, _half), Add(_centre.Y, _half), bottom, top);
            }
            Number y = Add(_centre.Y, Mul(C(part == SupportNeighborhoodCurved.TopCap ? _axisSign : -_axisSign), _half));
            return Overlap(y, y, bottom, top);
        }
        bool side = part == SupportNeighborhoodCurved.Side;
        Number t = side ? CoreParameter() : Mul(C(part == SupportNeighborhoodCurved.TopCap ? 1 : -1), _half);
        Number square = Sub(Sq(disc), Sq(Sub(_p, t)));
        GeometrySign extent = Sign(square);
        if (extent == GeometrySign.Negative || extent == GeometrySign.Unresolved) return extent;
        Number width = Root(square), near = Max(Sub(Abs(_q), width), C(0));
        GeometrySign meets = Sign(Sub(_radius, near));
        if (meets == GeometrySign.Negative || meets == GeometrySign.Unresolved) return meets;
        Number high = Root(Sub(Sq(_radius), Sq(near)));
        if (!side) return Overlap(Sub(_centre.Y, high), Add(_centre.Y, high), bottom, top);
        Number far = Min(Add(Abs(_q), width), _radius);
        Number low = Root(Sub(Sq(_radius), Sq(far)));
        GeometrySign upper = Overlap(Add(_centre.Y, low), Add(_centre.Y, high), bottom, top);
        GeometrySign lower = Overlap(Sub(_centre.Y, high), Sub(_centre.Y, low), bottom, top);
        if (upper == GeometrySign.Positive || lower == GeometrySign.Positive) return GeometrySign.Positive;
        return upper == GeometrySign.Unresolved || lower == GeometrySign.Unresolved ? GeometrySign.Unresolved : GeometrySign.Negative;
    }

    GeometrySign RoundAdmission(Number disc, Number bottom, Number top)
    {
        Number yLow = _centre.Y, yHigh = _centre.Y, nearestSquare = _distanceSquared;
        if (!_sphere && _upright) { yLow = Sub(yLow, _half); yHigh = Add(yHigh, _half); }
        else if (!_sphere)
        {
            Number gap = Sub(_p, CoreParameter());
            nearestSquare = Add(Sq(_q), Sq(gap));
        }
        Number radial = Max(Sub(Root(nearestSquare), disc), C(0));
        Number vertical = Max(Max(Sub(bottom, yHigh), Sub(yLow, top)), C(0));
        GeometrySign minimum = Sign(Sub(Add(Sq(radial), Sq(vertical)), Sq(_radius)));
        if (minimum == GeometrySign.Positive) return GeometrySign.Negative;
        if (minimum == GeometrySign.Unresolved) return minimum;
        Number maximum;
        if (_sphere || _upright)
        {
            Number far = Add(Root(_distanceSquared), disc);
            Number below = Max(Sub(yLow, bottom), C(0)), above = Max(Sub(top, yHigh), C(0));
            maximum = Add(Sq(far), Max(Sq(below), Sq(above)));
        }
        else
        {
            if (!HorizontalCoreMaximum(disc, out Number square)) return GeometrySign.Unresolved;
            Number verticalFar = Max(Abs(Sub(bottom, _centre.Y)), Abs(Sub(top, _centre.Y)));
            maximum = Add(square, Sq(verticalFar));
        }
        GeometrySign outside = Sign(Sub(maximum, Sq(_radius)));
        return outside == GeometrySign.Zero ? GeometrySign.Positive : outside;
    }

    // A convex segment-distance maximum on a disc occurs in the finite strip, or beyond an end.
    // The strip's lateral crest and the feasible farthest point from each end exhaust those branches.
    bool HorizontalCoreMaximum(Number disc, out Number maximum)
    {
        maximum = default;
        bool hasPoint = false;
        Number t = CoreParameter(), stripSquare = Sub(Sq(disc), Sq(Sub(_p, t)));
        GeometrySign strip = Sign(stripSquare);
        if (strip == GeometrySign.Unresolved) return false;
        if (strip != GeometrySign.Negative)
        {
            maximum = Sq(Add(Abs(_q), Root(stripSquare)));
            hasPoint = true;
        }
        // When the disc centre lies in the finite strip, every outside-end maximum
        // is on its seam and is already bounded by the attained lateral crest.
        if (t.Id == _p.Id) return hasPoint && Bounds(maximum).IsResolved;
        for (int direction = -1; direction <= 1; direction += 2)
        {
            Number end = Mul(C(direction), _half), offset = Sub(_p, end);
            Number square = Add(Sq(offset), Sq(_q)), distance = Root(square);
            // The farthest rim's axial offset is offset*(1+disc/distance), with a positive factor.
            GeometrySign beyond = Sign(Mul(C(direction), offset));
            if (beyond == GeometrySign.Unresolved) return false;
            if (beyond == GeometrySign.Negative) continue;
            Number candidate = Sq(Add(distance, disc));
            maximum = hasPoint ? Max(maximum, candidate) : candidate;
            hasPoint = true;
        }
        return hasPoint && Bounds(maximum).IsResolved;
    }
}
