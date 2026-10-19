using System;
using Number = KhaozEngine.Physics.Bepu.SupportCurvedSourceArithmetic.Number;

namespace KhaozEngine.Physics.Bepu;

internal sealed partial class SupportTiltedCurvedGeometry
{
    internal bool HasFiniteAdmissionWitness(SupportCurvedShape shape, int capKind, double x, double z,
        double radius, double bottom, double top, bool requireUpward)
    {
        if (_work.Failed || shape != SupportCurvedShape.Cylinder || capKind is not (-1 or 2 or 3)) return false;
        if (requireUpward && capKind >= 0 && !CapUniformlyUp(capKind)) return false;
        // The nearest part point is a binary64 candidate only. SurfacePointInside proves the offered
        // dyadic point exactly, so a hit proves existence on this part, never coverage or a maximum.
        double dx = _centre.X.Lower - x, dz = _centre.Z.Lower - z;
        double ex = Middle(_e.X), ez = Middle(_e.Z);
        double along = dx * ex + dz * ez, lateral = dz * ex - dx * ez;
        double u = Middle(B(_u)), k = Middle(B(_k)), r = Middle(B(_r)), h = Middle(B(_h));
        if (capKind < 0)
        {
            // Begin proves u > 0, so kind 0 at b = 0 is the upward top line. Its XZ shadow runs along e.
            double a = (r * k - along) / (h * u);
            return _work.Complete(Offer(0, double.IsNaN(a) ? 0 : Math.Clamp(a, -1, 1), 0, x, z, radius, bottom, top));
        }
        double ca = (along + (capKind == 2 ? h : -h) * u) / (r * k), cb = -lateral / r;
        if (!double.IsFinite(ca)) ca = 0;
        if (!double.IsFinite(cb)) cb = 0;
        if (BoundedGeometryArithmetic.CompareSumOfFourSquares(ca, cb, 0, 0, 1) == GeometrySign.Positive)
        {
            // Outside the disc, offer the rim point toward the vertical projection of the foot centre.
            double length = Math.Sqrt(ca * ca + cb * cb);
            ca /= length; cb /= length;
            if (BoundedGeometryArithmetic.CompareSumOfFourSquares(ca, cb, 0, 0, 1) == GeometrySign.Positive)
            {
                ca *= 1 - Math.ScaleB(1, -50); cb *= 1 - Math.ScaleB(1, -50);
            }
        }
        return _work.Complete(Offer(capKind, ca, cb, x, z, radius, bottom, top));
    }

    bool Offer(int kind, double a, double b, double x, double z, double radius, double bottom, double top)
    {
        // A candidate whose enclosure misses the column is declined before any source work.
        GeometryVector position = Surface(kind, I(a), I(b));
        if (position.IsResolved && (position.Y.Lower > top || position.Y.Upper < bottom ||
            position.X.Subtract(I(x)).Square().Add(position.Z.Subtract(I(z)).Square()).Lower > I(radius).Square().Upper))
            return false;
        return SurfacePointInside(kind, a, b, x, z, radius, bottom, top);
    }

    static double Middle(GeometryInterval v) => v.Lower + (v.Upper - v.Lower) * 0.5;

    readonly record struct Vector(Number X, Number Y, Number Z);
    Vector Add(Vector a, Vector b) => new(Add(a.X, b.X), Add(a.Y, b.Y), Add(a.Z, b.Z));
    Vector Scale(Vector a, Number b) => new(Mul(a.X, b), Mul(a.Y, b), Mul(a.Z, b));
    internal int CapKind(int part) => ((part == SupportNeighborhoodCurved.TopCap ? 1 : -1) * _direction) > 0 ? 2 : 3;
    internal bool CapFacesDown(int part) => CapUpward(CapKind(part)).Upper < 0;
    internal GeometryInterval CapUpward(int kind) => _work.Complete(kind == 2 ? _axis.Y : N(_axis.Y));
    GeometryInterval NormalizedYBounds()
    {
        GeometryInterval bounds = B(_k), source = B(_arithmetic.Y);
        if (!bounds.IsResolved || !source.IsResolved) return default;
        // The positive normalizing length preserves the installed source's weak sign.
        // Arithmetic rounding across zero does not invalidate that component certificate.
        double lower = bounds.Lower, upper = bounds.Upper;
        if (_direction > 0 ? source.Lower >= 0 : source.Upper <= 0) lower = Math.Max(0, lower);
        if (_direction > 0 ? source.Upper <= 0 : source.Lower >= 0) upper = Math.Min(0, upper);
        return _work.Complete(GeometryInterval.Enclose(lower, upper));
    }
    GeometryVector CapNormal(int part)
    {
        int kind = CapKind(part);
        GeometryVector normal = Scale(_axis, I(kind == 2 ? 1 : -1));
        return _work.Complete(new GeometryVector(normal.X, CapUpward(kind), normal.Z));
    }
    internal bool CapNeedsUpperCoverage(int part)
    {
        GeometryInterval upward = CapUpward(CapKind(part));
        return _work.Complete(upward.Lower < 0 && upward.Upper >= 0);
    }
    internal bool CapUniformlyUp(int kind)
    {
        if (_work.Failed) return false;
        GeometrySign sign = Sign(Mul(_k, C(kind == 2 ? 1 : -1)));
        return _work.Complete(sign is GeometrySign.Positive or GeometrySign.Zero || CapUpward(kind).Lower >= 0);
    }

    internal GeometryVector Surface(int kind, GeometryInterval a, GeometryInterval b)
    {
        if (_work.Failed) return default;
        if (kind is 2 or 3)
            return _work.Complete(Add(Add(_centre, Scale(_axis, B(_h).Multiply(I(kind == 2 ? 1 : -1)))),
                Scale(Add(Scale(_w, a), Scale(_f, b)), B(_r))));
        GeometryInterval v = PositiveRoot(I(1).Subtract(b.Square()));
        if ((kind & 1) != 0) v = N(v);
        GeometryVector radial = Add(Scale(_w, v), Scale(_f, b));
        if (kind < 4) return _work.Complete(Add(Add(_centre, Scale(_axis, B(_h).Multiply(a))), Scale(radial, B(_r))));
        int end = kind < 6 ? 1 : -1;
        GeometryInterval h = a.Add(I(1)).Multiply(I(0.5)), lateral = PositiveRoot(I(1).Subtract(h.Square()));
        GeometryVector normal = Add(Scale(_axis, h.Multiply(I(end))), Scale(radial, lateral));
        return _work.Complete(Add(Add(_centre, Scale(_axis, B(_h).Multiply(I(end)))), Scale(normal, B(_r))));
    }

    internal bool SurfacePointInside(int kind, double a, double b, double x, double z, double radius,
        double bottom, double top)
    {
        if (_work.Failed) return false;
        if (kind is 2 or 3 && BoundedGeometryArithmetic.CompareSumOfFourSquares(a, b, 0, 0, 1) == GeometrySign.Positive)
            return false;
        GeometryVector position = Surface(kind, I(a), I(b));
        GeometryInterval distance = position.X.Subtract(I(x)).Square().Add(position.Z.Subtract(I(z)).Square());
        if (position.IsResolved && position.Y.Lower >= bottom && position.Y.Upper <= top &&
            distance.Upper <= I(radius).Square().Lower) return _work.Complete(true);
        int checkpoint = _arithmetic.Checkpoint;
        Vector sample = SourceSurface(kind, C(a), C(b));
        GeometrySign inside = Sign(Sub(Sq(C(radius)), Add(Sq(Sub(sample.X, C(x))), Sq(Sub(sample.Z, C(z))))));
        GeometrySign above = Sign(Sub(sample.Y, C(bottom))), below = Sign(Sub(C(top), sample.Y));
        _arithmetic.Restore(checkpoint);
        return _work.Complete(inside is GeometrySign.Positive or GeometrySign.Zero &&
            above is GeometrySign.Positive or GeometrySign.Zero && below is GeometrySign.Positive or GeometrySign.Zero);
    }

    Vector SourceSurface(int kind, Number a, Number b)
    {
        var centre = new Vector(C(_centre.X.Lower), C(_centre.Y.Lower), C(_centre.Z.Lower));
        var axis = new Vector(_axisX, _k, _axisZ);
        var w = new Vector(Neg(Mul(_k, _eX)), _u, Neg(Mul(_k, _eZ)));
        var f = new Vector(Neg(_eZ), C(0), _eX);
        if (kind is 2 or 3)
            return Add(Add(centre, Scale(axis, Mul(_h, C(kind == 2 ? 1 : -1)))),
                Scale(Add(Scale(w, a), Scale(f, b)), _r));
        var v = Root(Sub(C(1), Sq(b)));
        if ((kind & 1) != 0) v = Neg(v);
        var radial = Add(Scale(w, v), Scale(f, b));
        if (kind < 4) return Add(Add(centre, Scale(axis, Mul(_h, a))), Scale(radial, _r));
        int end = kind < 6 ? 1 : -1;
        Number h = Mul(Add(a, C(1)), C(0.5)), lateral = Root(Sub(C(1), Sq(h)));
        var normal = Add(Scale(axis, Mul(h, C(end))), Scale(radial, lateral));
        return Add(Add(centre, Scale(axis, Mul(_h, C(end)))), Scale(normal, _r));
    }
}
