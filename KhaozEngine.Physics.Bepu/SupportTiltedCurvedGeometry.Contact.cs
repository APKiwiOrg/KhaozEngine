using System;

namespace KhaozEngine.Physics.Bepu;

internal sealed partial class SupportTiltedCurvedGeometry : ISupportCurvedRootFunction
{
    SupportCurvedShape _contactShape;
    int _contactPart;
    double _probe;
    double _contactLow, _contactHigh;

    internal GeometrySign Contact(SupportCurvedShape shape, int part, double probe, double bandLow, double bandHigh,
        out GeometryInterval lowest)
    {
        lowest = default;
        if (_work.Failed) return GeometrySign.Unresolved;
        _contactShape = shape; _contactPart = part; _probe = probe;
        _boundaryMode = false;
        _stationaryMode = false;
        _axisMode = false;
        // A second probe radius is proof slack, outside every possible contact centre.
        // It gives strictly positive endpoint signs without enlarging the published reach.
        GeometryInterval reach = B(_h).Multiply(B(_arithmetic.Abs(_k))).Add(B(_r)).Add(I(probe).Multiply(I(2)));
        double low = _centre.Y.Subtract(reach).Lower, high = _centre.Y.Add(reach).Upper;
        _contactLow = low; _contactHigh = high;
        if (!SolveRoots(low, high)) return GeometrySign.Unresolved;
        if (_roots.Count == 0) return GeometrySign.Negative;
        GeometryInterval root = _roots.Get(_roots.Count - 1);
        lowest = root.Subtract(I(probe));
        for (int boundary = 0; boundary < 2; boundary++)
        {
            double band = boundary == 0 ? bandLow : bandHigh;
            double centre = band + probe;
            if (centre >= root.Lower && centre <= root.Upper && SignContact(centre) == GeometrySign.Zero)
                lowest = I(band);
        }
        return lowest.IsResolved ? GeometrySign.Positive : GeometrySign.Unresolved;
    }

    GeometryInterval ISupportCurvedRootFunction.Evaluate(GeometryInterval y, bool derivative)
    {
        if (_axisMode) return AxisValue(y, derivative);
        if (_stationaryMode) return StationaryValue(y, derivative);
        if (_boundaryMode) return BoundaryValue(y, derivative);
        GeometryInterval dy = y.Subtract(_centre.Y), u = B(_u), k = _axis.Y, p = B(_p);
        GeometryInterval t = k.Multiply(dy).Subtract(u.Multiply(p));
        GeometryInterval across = u.Multiply(dy).Add(k.Multiply(p));
        GeometryInterval radialSquare = across.Square().Add(B(_q).Square());
        GeometryInterval radial = PositiveRoot(radialSquare);
        GeometryInterval gap, gapDerivative;
        if (_contactShape == SupportCurvedShape.Cylinder && _contactPart != SupportNeighborhoodCurved.Side)
        {
            int direction = (_contactPart == SupportNeighborhoodCurved.TopCap ? 1 : -1) * _direction;
            gap = t.Subtract(B(_h).Multiply(I(direction)));
            gapDerivative = k;
        }
        else
        {
            double half = B(_h).Lower;
            double furthest = Math.Max(Math.Abs(t.Lower), Math.Abs(t.Upper));
            double nearest = t.Lower <= 0 && t.Upper >= 0 ? 0 : Math.Min(Math.Abs(t.Lower), Math.Abs(t.Upper));
            GeometryInterval rawGap = GeometryInterval.Enclose(nearest, furthest).Subtract(B(_h));
            gap = GeometryInterval.Enclose(Math.Max(0, rawGap.Lower), Math.Max(0, rawGap.Upper));
            gapDerivative = t.Lower >= half ? k : t.Upper <= -half ? N(k) :
                gap.Upper == 0 ? I(0) : Hull(N(k), k);
        }
        if (_contactShape == SupportCurvedShape.Capsule)
            return derivative ? I(2).Multiply(across.Multiply(u).Add(gap.Multiply(gapDerivative))) :
                radialSquare.Add(gap.Square()).Subtract(B(_r).Add(I(_probe)).Square());
        GeometryInterval radialGap = radial.Subtract(B(_r));
        if (_contactPart != SupportNeighborhoodCurved.Side)
            radialGap = GeometryInterval.Enclose(Math.Max(0, radialGap.Lower), Math.Max(0, radialGap.Upper));
        if (!derivative) return radialGap.Square().Add(gap.Square()).Subtract(I(_probe).Square());
        GeometryInterval radialTerm = radialGap.Lower == 0 && radialGap.Upper == 0 ? I(0) :
            radialGap.Multiply(across.Multiply(u).Divide(radial));
        return I(2).Multiply(radialTerm.Add(gap.Multiply(gapDerivative)));
    }

    GeometrySign ISupportCurvedRootFunction.Sign(double parameter) => _axisMode ? AxisSign(parameter) :
        _stationaryMode ? StationarySign(parameter) :
        _boundaryMode ? BoundarySign(parameter) : SignContact(parameter);
    GeometrySign ISupportCurvedRootFunction.DerivativeSign(GeometryInterval parameter) =>
        _axisMode ? GeometrySign.Unresolved : _stationaryMode ? StationaryDerivativeSign(parameter) :
        _boundaryMode ? BoundaryDerivativeSign(parameter) : GeometrySign.Unresolved;

    GeometrySign SignContact(double y)
    {
        if (_work.Failed) return GeometrySign.Unresolved;
        // Unit-axis surface extents enclose the whole raw part. Here vertical separation
        // is at least two probe radii, so the contact objective is strictly positive.
        if (y <= _contactLow || y >= _contactHigh) return GeometrySign.Positive;
        int checkpoint = _arithmetic.Checkpoint;
        var dy = Sub(C(y), C(_centre.Y.Lower));
        var t = Sub(Mul(_k, dy), Mul(_u, _p));
        var radialSquare = Add(Sq(Add(Mul(_u, dy), Mul(_k, _p))), Sq(_q));
        var gap = _contactShape == SupportCurvedShape.Cylinder && _contactPart != SupportNeighborhoodCurved.Side ?
            Sub(t, Mul(C((_contactPart == SupportNeighborhoodCurved.TopCap ? 1 : -1) * _direction), _h)) :
            _arithmetic.Max(Sub(_arithmetic.Abs(t), _h), C(0));
        var value = _contactShape == SupportCurvedShape.Capsule ?
            Sub(Add(radialSquare, Sq(gap)), Sq(Add(_r, C(_probe)))) :
            Sub(Add(Sq(_contactPart == SupportNeighborhoodCurved.Side ? Sub(Root(radialSquare), _r) :
                _arithmetic.Max(Sub(Root(radialSquare), _r), C(0))), Sq(gap)), Sq(C(_probe)));
        GeometrySign result = Sign(value);
        _arithmetic.Restore(checkpoint);
        return result;
    }
}
