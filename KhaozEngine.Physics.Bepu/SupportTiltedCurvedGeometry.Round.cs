using System;

namespace KhaozEngine.Physics.Bepu;

internal sealed partial class SupportTiltedCurvedGeometry
{
    internal bool TryGlobalCrown(double slope, out Witness witness, out GeometryInterval maximum)
    {
        witness = default; maximum = default;
        if (_work.Failed) return false;
        GeometrySign vertical = Sign(_k);
        if (vertical is not (GeometrySign.Positive or GeometrySign.Negative)) return false;
        int end = vertical == GeometrySign.Positive ? 1 : -1;
        var p = Add(_p, Mul(_u, Mul(C(end), _h)));
        var distance = Root(Add(Sq(p), Sq(_q)));
        var nearest = _arithmetic.Max(Sub(distance, _disc), C(0));
        if (Sign(nearest) != GeometrySign.Zero || Sign(_r) != GeometrySign.Positive) return false;
        if (RoundEnd(end, false, slope, out var crown) != GeometrySign.Positive) return false;
        // Zero nearest gap makes RoundEnd's radial vector (0, radius, 0). The signed higher
        // endpoint therefore attains centre.Y + abs(axis.Y) * halfLength + radius for the whole capsule.
        var global = B(Add(Add(C(_centre.Y.Lower), Mul(_arithmetic.Abs(_k), _h)), _r));
        if (!global.IsResolved) return false;
        witness = crown; maximum = global;
        return _work.Complete(true, ref witness, ref maximum);
    }

    internal GeometrySign Round(bool axis, double slope, out Witness witness, out GeometryInterval maximum)
    {
        witness = default; maximum = default;
        if (_work.Failed) return _work.Complete(GeometrySign.Unresolved, ref witness, ref maximum);
        if (!axis && TryGlobalCrown(slope, out witness, out maximum))
            return _work.Complete(GeometrySign.Positive, ref witness, ref maximum);
        GeometrySign side = axis ? Axis(false, 0, out witness) : Side(slope, out witness, out _);
        maximum = side == GeometrySign.Positive ? witness.Position.Y : default;
        if (side == GeometrySign.Unresolved) return _work.Complete(side, ref witness, ref maximum);
        bool any = side == GeometrySign.Positive;
        for (int end = -1; end <= 1; end += 2)
        {
            GeometrySign offered = RoundEnd(end, axis, slope, out Witness point);
            if (offered == GeometrySign.Unresolved) return _work.Complete(offered, ref witness, ref maximum);
            if (offered != GeometrySign.Positive) continue;
            if (!any || point.Position.Y.Lower > maximum.Upper)
            { witness = point; maximum = point.Position.Y; }
            else if (point.Position.Y.Upper >= maximum.Lower)
            {
                witness = new(Hull(witness.Position, point.Position), Hull(witness.Normal, point.Normal),
                    witness.AtSideCrest && point.AtSideCrest);
                maximum = GeometryInterval.Enclose(Math.Max(maximum.Lower, point.Position.Y.Lower),
                    Math.Max(maximum.Upper, point.Position.Y.Upper));
            }
            any = true;
        }
        return _work.Complete(any ? GeometrySign.Positive : GeometrySign.Negative, ref witness, ref maximum);
    }

    GeometrySign RoundEnd(int end, bool axis, double slope, out Witness witness)
    {
        witness = default;
        var t = Mul(C(end), _h);
        var p = Add(_p, Mul(_u, t));
        var distance = Root(Add(Sq(p), Sq(_q)));
        var nearest = axis ? distance : _arithmetic.Max(Sub(distance, _disc), C(0));
        var exists = Sign(Sub(_r, nearest));
        if (exists == GeometrySign.Negative) return _work.Complete(exists, ref witness);
        if (exists == GeometrySign.Unresolved)
        {
            GeometryInterval conditionalHeight = PositiveRoot(B(_r).Square().Subtract(B(nearest).Square()));
            if (conditionalHeight.IsResolved && B(distance).Lower > 0)
            {
                GeometryInterval along = B(_u).Multiply(N(B(p))).Multiply(B(nearest).Divide(B(distance)))
                    .Add(_axis.Y.Multiply(conditionalHeight)).Multiply(I(end));
                if (along.Upper < 0) return _work.Complete(GeometrySign.Negative, ref witness);
            }
            return _work.Complete(exists, ref witness);
        }
        var height = Root(Sub(Sq(_r), Sq(nearest)));
        var factor = axis ? C(1) : Sign(nearest) == GeometrySign.Zero ? C(0) :
            Div(nearest, _arithmetic.Max(distance, _disc));
        var parallel = Neg(Mul(p, factor)); var lateral = Neg(Mul(_q, factor));
        var hemisphere = Sign(Mul(C(end), Add(Mul(_u, parallel), Mul(_k, height))));
        if (hemisphere is GeometrySign.Negative or GeometrySign.Unresolved) return _work.Complete(hemisphere, ref witness);
        var normalY = Div(height, _r);
        var walkable = Sign(Sub(normalY, C(slope)));
        if (walkable is GeometrySign.Negative or GeometrySign.Unresolved) return _work.Complete(walkable, ref witness);
        GeometryInterval upward = walkable == GeometrySign.Zero ? I(slope) : B(normalY);
        upward = GeometryInterval.Enclose(Math.Max(slope, upward.Lower), upward.Upper);
        var radial = Add(Add(Scale(_e, B(parallel)), Scale(_f, B(lateral))),
            new GeometryVector(I(0), B(height), I(0)));
        var normal = Scale(radial, I(1).Divide(B(_r)));
        normal = new(normal.X, upward, normal.Z);
        witness = new(Add(Add(_centre, Scale(_axis, B(t))), radial), normal);
        return _work.Complete(witness.Position.IsResolved && normal.IsResolved ? GeometrySign.Positive : GeometrySign.Unresolved, ref witness);
    }
}
