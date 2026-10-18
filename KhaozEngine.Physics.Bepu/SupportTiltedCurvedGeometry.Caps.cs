using System;

namespace KhaozEngine.Physics.Bepu;

internal sealed partial class SupportTiltedCurvedGeometry
{
    internal void RefineCapUpward(int part, double slope, ref Witness witness)
    {
        GeometrySign sign = Sign(Sub(Mul(_k, C(CapKind(part) == 2 ? 1 : -1)), C(slope)));
        if (sign is GeometrySign.Negative or GeometrySign.Unresolved) return;
        GeometryInterval upward = sign == GeometrySign.Zero ? I(slope) :
            GeometryInterval.Enclose(Math.Max(slope, witness.Normal.Y.Lower), witness.Normal.Y.Upper);
        witness = new(witness.Position, new(witness.Normal.X, upward, witness.Normal.Z));
    }

    internal GeometrySign Axis(bool cap, int part, out Witness witness)
    {
        witness = default;
        if (_work.Failed) return GeometrySign.Unresolved;
        if (cap)
        {
            int direction = (part == SupportNeighborhoodCurved.TopCap ? 1 : -1) * _direction;
            if (CapFacesDown(part)) return GeometrySign.Negative;
            double end = direction * B(_h).Lower;
            var p = Add(_p, Mul(_u, C(end)));
            var z = Div(Neg(_q), _r);
            GeometryInterval vertical;
            if (_axis.Y.Lower <= 0 && _axis.Y.Upper >= 0)
            {
                var square = Sub(C(1), Sq(z));
                var exists = Sign(square);
                if (exists is GeometrySign.Negative or GeometrySign.Unresolved) return exists;
                var circle = B(Root(square));
                var result = AxisParameter(Mul(_r, _k), p, -circle.Upper, circle.Upper, out vertical);
                if (result != GeometrySign.Positive) return result;
            }
            else
            {
                var v = Div(p, Mul(_r, _k));
                var exists = Sign(Sub(C(1), Add(Sq(v), Sq(z))));
                if (exists is GeometrySign.Negative or GeometrySign.Unresolved) return exists;
                vertical = B(v);
            }
            witness = new(Add(Add(_centre, Scale(_axis, I(end))),
                Scale(Add(Scale(_w, vertical), Scale(_f, B(z))), B(_r))), CapNormal(part));
        }
        else
        {
            var square = Sub(Sq(_r), Sq(_q));
            var exists = Sign(square);
            if (exists is GeometrySign.Negative or GeometrySign.Unresolved) return exists;
            var radial = Root(square);
            // Clear the shared normalizing denominator before the finite-axis source predicate.
            var offset = Sub(Mul(_sourceY, radial), Mul(_p, _length));
            var t = Div(offset, _horizontal);
            var finite = Sign(Sub(_h, _arithmetic.Abs(t)));
            if (finite == GeometrySign.Negative) return finite;
            GeometryInterval axial = B(t);
            if (finite == GeometrySign.Unresolved)
            {
                var result = AxisParameter(_horizontal, offset, -B(_h).Lower, B(_h).Lower, out axial);
                if (result != GeometrySign.Positive) return result;
            }
            var normal = Add(Scale(_w, B(Div(radial, _r))), Scale(_f, B(Div(Neg(_q), _r))));
            witness = new(Add(Add(_centre, Scale(_axis, axial)), Scale(normal, B(_r))), normal);
        }
        return witness.Position.IsResolved && witness.Normal.IsResolved ? GeometrySign.Positive : GeometrySign.Unresolved;
    }

    internal GeometrySign Cap(int part, bool minimum, out Witness witness, out GeometryInterval maximum)
    {
        witness = default; maximum = default;
        if (_work.Failed) return GeometrySign.Unresolved;
        _count = 0;
        double end = (part == SupportNeighborhoodCurved.TopCap ? 1 : -1) * _direction * B(_h).Lower;
        GeometryVector normal = CapNormal(part);
        if (!Keep(I(0)) || !Keep(B(Div(Neg(_q), _r))) || !Keep(I(-1)) || !Keep(I(1)) ||
            !Keep(B(Div(Sub(Neg(_disc), _q), _r))) || !Keep(B(Div(Sub(_disc, _q), _r))) ||
            !Boundaries(end, 1) || !Boundaries(end, -1)) return GeometrySign.Unresolved;
        bool any = false, possible = false;
        double best = double.NegativeInfinity;
        for (int i = 0; i < _count; i++)
        {
            GeometrySign feasible = CapPoint(_candidates[i], end, minimum, out Witness point, out bool certain);
            if (feasible == GeometrySign.Unresolved) return feasible;
            if (feasible != GeometrySign.Positive) continue;
            possible = true;
            if (certain) best = Math.Max(best, minimum ? -point.Position.Y.Upper : point.Position.Y.Lower);
        }
        if (!possible) return GeometrySign.Negative;
        if (!double.IsFinite(best)) return GeometrySign.Unresolved;
        double upper = best;
        for (int i = 0; i < _count; i++)
        {
            if (CapPoint(_candidates[i], end, minimum, out Witness point, out _) != GeometrySign.Positive) continue;
            double bound = minimum ? -point.Position.Y.Lower : point.Position.Y.Upper;
            if (bound < best) continue;
            witness = any ? new(Hull(witness.Position, point.Position), normal) : new(point.Position, normal);
            any = true; upper = Math.Max(upper, bound);
        }
        maximum = minimum ? GeometryInterval.Enclose(-upper, -best) : GeometryInterval.Enclose(best, upper);
        return any ? GeometrySign.Positive : GeometrySign.Unresolved;
    }

    GeometrySign CapPoint(Candidate candidate, double end, bool minimum, out Witness witness, out bool certain)
    {
        witness = default; certain = false;
        GeometryInterval z = candidate.Parameter, circle = PositiveRoot(I(1).Subtract(z.Square()));
        GeometryInterval p = B(_p).Add(B(_u).Multiply(I(end))), lateral = B(_q).Add(B(_r).Multiply(z));
        GeometryInterval square = B(_disc).Square().Subtract(lateral.Square());
        if (!circle.IsResolved || !square.IsResolved || square.Upper < 0) return GeometrySign.Negative;
        GeometryInterval extent = PositiveRoot(square), divisor = B(_r).Multiply(_axis.Y);
        GeometryInterval v;
        if (candidate.Rim != 0)
        {
            v = candidate.Rim < 0 ? N(circle) : circle;
            certain = true;
        }
        else if (divisor.Lower <= 0 && divisor.Upper >= 0)
        {
            GeometryInterval parallel = p.Subtract(divisor.Multiply(minimum ? N(circle) : circle));
            if (parallel.Square().Add(lateral.Square()).Upper <= B(_disc).Square().Lower)
            {
                v = minimum ? N(circle) : circle;
                certain = true;
            }
            else
            {
                GeometryInterval all = Hull(N(circle), circle);
                if (p.Subtract(divisor.Multiply(all)).Square().Add(lateral.Square()).Lower > B(_disc).Square().Upper)
                    return GeometrySign.Negative;
                v = all;
            }
        }
        else
        {
            GeometryInterval low = p.Subtract(extent).Divide(divisor), high = p.Add(extent).Divide(divisor);
            if (divisor.Upper < 0) (low, high) = (high, low);
            if (low.Lower > circle.Upper || high.Upper < -circle.Upper) return GeometrySign.Negative;
            v = minimum ? GeometryInterval.Enclose(Math.Max(-circle.Upper, low.Lower), Math.Max(-circle.Lower, low.Upper)) :
                GeometryInterval.Enclose(Math.Min(circle.Lower, high.Lower), Math.Min(circle.Upper, high.Upper));
            certain = square.Lower >= 0 && low.Upper <= circle.Lower && high.Lower >= -circle.Lower;
        }
        var radial = Add(Scale(_w, v), Scale(_f, z));
        witness = new(Add(Add(_centre, Scale(_axis, I(end))), Scale(radial, B(_r))), _axis);
        return witness.Position.IsResolved ? GeometrySign.Positive : GeometrySign.Unresolved;
    }
}
