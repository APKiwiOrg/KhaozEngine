using System;
using Number = KhaozEngine.Physics.Bepu.SupportCurvedSourceArithmetic.Number;

namespace KhaozEngine.Physics.Bepu;

internal sealed partial class SupportTiltedCurvedGeometry
{
    bool _stationaryMode;
    Number _absoluteK;

    bool SignedStationary()
    {
        _stationaryMode = true; _boundaryMode = _axisMode = false;
        _absoluteK = _arithmetic.Abs(_k);
        GeometryInterval other = B(Div(Neg(_q), _r));
        GeometryInterval left = B(Div(Sub(Neg(_disc), _q), _r));
        GeometryInterval right = B(Div(Sub(_disc, _q), _r));
        double low = Math.Max(-1, Math.Min(0, other.Lower));
        double high = Math.Min(1, Math.Max(0, other.Upper));
        // Extend past zero within the common smooth domain. Every source, including
        // k=0, then has a strict existence bracket instead of an uncertain endpoint.
        double domainLow = Math.Max(-1, left.Upper), domainHigh = Math.Min(1, right.Lower);
        low = Math.Max(domainLow, domainLow + (low - domainLow) * 0.5);
        high = Math.Min(domainHigh, high + (domainHigh - high) * 0.5);
        if (low < high)
        {
            if (!SolveRoots(low, high, maximumRoots: 1)) return false;
            // A uniform existence bracket and strict monotonicity cover every source's
            // unique stationary point over its entire smooth domain, including the tails.
            if (_roots.Count == 1) return _work.Complete(Keep(_roots.Get(0)));
        }
        // A common-domain absence certificate does not cover source-dependent domain
        // tails. Retain their union and let the bounded region owner certify or refuse it.
        if (!SolveRoots(Math.Max(-1, left.Lower), Math.Min(1, right.Upper), maximumRoots: 1)) return false;
        for (int i = 0; i < _roots.Count; i++)
            if (!Keep(_roots.Get(i))) return false;
        return _work.Complete(true);
    }

    GeometryInterval StationaryValue(GeometryInterval z, bool derivative)
    {
        GeometryInterval v = PositiveRoot(I(1).Subtract(z.Square()));
        GeometryInterval lateral = B(_q).Add(B(_r).Multiply(z));
        GeometryInterval extent = PositiveRoot(B(_disc).Square().Subtract(lateral.Square()));
        if (!derivative) return _work.Complete(z.Divide(v).Add(B(_absoluteK).Multiply(lateral).Divide(extent)));
        return _work.Complete(I(1).Divide(v.Square().Multiply(v)).Add(B(_absoluteK).Multiply(B(_r))
            .Multiply(B(_disc).Square()).Divide(extent.Square().Multiply(extent))));
    }

    GeometrySign StationarySign(double parameter)
    {
        int checkpoint = _arithmetic.Checkpoint;
        Number z = C(parameter), lateral = Add(_q, Mul(_r, z));
        // The positive denominators may vanish at a closed endpoint. The unsquared
        // numerator preserves the one-sided sign and every source's horizontal root.
        Number v = Root(Sub(C(1), Sq(z)));
        Number extent = Root(Sub(Sq(_disc), Sq(lateral)));
        GeometrySign sign = Sign(Add(Mul(z, extent), Mul(Mul(_absoluteK, lateral), v)));
        _arithmetic.Restore(checkpoint);
        return _work.Complete(sign);
    }

    GeometrySign StationaryDerivativeSign(GeometryInterval z)
    {
        GeometryInterval lateral = B(_q).Add(B(_r).Multiply(z));
        // On the open disc branch, d(z/v)/dz=1/v^3 and the other term
        // has nonnegative derivative. Continuity keeps its closed endpoint roots.
        return _work.Complete(z.Lower >= -1 && z.Upper <= 1 &&
            lateral.Lower >= -B(_disc).Lower && lateral.Upper <= B(_disc).Lower ?
            GeometrySign.Positive : GeometrySign.Unresolved);
    }
}
