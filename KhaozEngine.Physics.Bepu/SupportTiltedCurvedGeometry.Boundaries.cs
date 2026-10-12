using Number = KhaozEngine.Physics.Bepu.SupportCurvedSourceArithmetic.Number;

namespace KhaozEngine.Physics.Bepu;

internal sealed partial class SupportTiltedCurvedGeometry
{
    Number _boundaryB, _boundaryJ, _boundaryK;
    Number _boundaryP;
    int _boundaryRim;
    bool _boundaryMode;

    GeometryInterval BoundaryValue(GeometryInterval z, bool derivative)
    {
        GeometryInterval v = PositiveRoot(I(1).Subtract(z.Square()));
        if (!derivative)
        {
            GeometryInterval parallel = B(_boundaryP).Subtract(B(_r).Multiply(B(_k)).Multiply(v).Multiply(I(_boundaryRim)));
            GeometryInterval lateral = B(_q).Add(B(_r).Multiply(z));
            // Keep squared distances factored. At a pole the normal-square identity must
            // not be lost by separately enclosing R^2*k^2 and R^2*u^2.
            return parallel.Square().Add(lateral.Square()).Subtract(B(_disc).Square());
        }
        GeometryInterval linear = B(_boundaryB).Add(I(2).Multiply(B(_boundaryJ)).Multiply(z));
        if (B(_boundaryK).Lower == 0 && B(_boundaryK).Upper == 0) return linear;
        return linear.Add(B(_boundaryK).Multiply(z).Divide(v));
    }

    GeometrySign BoundarySign(double parameter)
    {
        int checkpoint = _arithmetic.Checkpoint;
        Number z = C(parameter), v = Root(Sub(C(1), Sq(z)));
        Number parallel = Sub(_boundaryP, Mul(Mul(Mul(_r, _k), v), C(_boundaryRim)));
        Number lateral = Add(_q, Mul(_r, z));
        GeometrySign result = Sign(Sub(Add(Sq(parallel), Sq(lateral)), Sq(_disc)));
        _arithmetic.Restore(checkpoint);
        return result;
    }

    GeometrySign BoundaryDerivativeSign(GeometryInterval z)
    {
        // At a pole the derivative may be unbounded. Its one-sided sign still certifies
        // strict monotonicity on the open interval, with continuity retaining the endpoint.
        GeometryInterval linear = B(_boundaryB).Add(I(2).Multiply(B(_boundaryJ)).Multiply(z));
        GeometryInterval numerator = B(_boundaryK).Multiply(z);
        if (linear.Lower > 0 && numerator.Lower >= 0) return GeometrySign.Positive;
        if (linear.Upper < 0 && numerator.Upper <= 0) return GeometrySign.Negative;
        int checkpoint = _arithmetic.Checkpoint;
        GeometrySign k = Sign(_boundaryK);
        _arithmetic.Restore(checkpoint);
        bool positive = k == GeometrySign.Zero || k == GeometrySign.Positive && z.Lower >= 0 || k == GeometrySign.Negative && z.Upper <= 0;
        bool negative = k == GeometrySign.Zero || k == GeometrySign.Positive && z.Upper <= 0 || k == GeometrySign.Negative && z.Lower >= 0;
        return linear.Lower > 0 && positive ? GeometrySign.Positive : linear.Upper < 0 && negative ? GeometrySign.Negative : GeometrySign.Unresolved;
    }
}
