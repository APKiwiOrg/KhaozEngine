using System;
using KhaozEngine.Physics;
using Number = KhaozEngine.Physics.Bepu.SupportCurvedSourceArithmetic.Number;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Finite tilted upper-side extrema. Boundary polynomials partition the nonconvex
/// projected domain. Source-uniform root regions enclose every stationary and boundary stratum.</summary>
internal sealed partial class SupportTiltedCurvedGeometry
{
    internal readonly record struct Witness(GeometryVector Position, GeometryVector Normal, bool AtSideCrest = false);
    enum DiscLateralBoundary : byte { None, Negative, Positive }
    readonly record struct Candidate(GeometryInterval Parameter, double End, bool AtEnd, bool Slope, int Rim,
        DiscLateralBoundary Lateral = DiscLateralBoundary.None);
    readonly SupportCurvedSourceArithmetic _arithmetic = new();
    readonly SupportCurvedRootRegions _roots = new();
    readonly Candidate[] _candidates = new Candidate[32];
    readonly Number[] _polynomial = new Number[5];
    Number _u, _k, _p, _q, _r, _h, _disc;
    Number _axisX, _axisZ, _eX, _eZ;
    Number _sourceY, _horizontal, _length;
    GeometryVector _axis, _e, _f, _w, _centre;
    int _count;
    int _direction;
    Number C(double v) => _arithmetic.Constant(v);
    Number Add(Number a, Number b) => _arithmetic.Add(a, b);
    Number Sub(Number a, Number b) => _arithmetic.Subtract(a, b);
    Number Mul(Number a, Number b) => _arithmetic.Multiply(a, b);
    Number Div(Number a, Number b) => _arithmetic.Divide(a, b);
    Number Sq(Number a) => _arithmetic.Square(a);
    Number Neg(Number a) => _arithmetic.Negate(a);
    Number Root(Number a) => _arithmetic.Root(a);
    GeometryInterval B(Number n) => _arithmetic.Bounds(n);
    GeometrySign Sign(Number n) => _arithmetic.Sign(n);
    static GeometryInterval I(double v) => GeometryInterval.Exact(v);
    static GeometryInterval N(GeometryInterval v) => GeometryInterval.Enclose(-v.Upper, -v.Lower);
    static GeometryInterval Hull(GeometryInterval a, GeometryInterval b) =>
        GeometryInterval.Enclose(Math.Min(a.Lower, b.Lower), Math.Max(a.Upper, b.Upper));
    static GeometryVector Hull(GeometryVector a, GeometryVector b) =>
        new(Hull(a.X, b.X), Hull(a.Y, b.Y), Hull(a.Z, b.Z));
    static GeometryVector Add(GeometryVector a, GeometryVector b) => SupportCurvedArithmetic.Add(a, b);
    static GeometryVector Scale(GeometryVector a, GeometryInterval b) => SupportCurvedArithmetic.Scale(a, b);
    static GeometryInterval PositiveRoot(GeometryInterval a) => a.IsResolved && a.Upper >= 0 ?
        GeometryInterval.Enclose(Math.Max(0, a.Lower), a.Upper).Sqrt() : default;

    internal bool Begin(in SupportCurvedLeaf leaf, double x, double z, double disc)
    {
        _arithmetic.Reset(leaf.SourceColumn);
        _axisMode = false;
        Number sx = _arithmetic.X, sy = _arithmetic.Y, sz = _arithmetic.Z;
        _direction = B(sy).Upper <= 0 ? -1 : 1;
        if (_direction < 0) { sx = Neg(sx); sy = Neg(sy); sz = Neg(sz); }
        Number horizontal = Sign(sz) == GeometrySign.Zero ? _arithmetic.Abs(sx) :
            Sign(sx) == GeometrySign.Zero ? _arithmetic.Abs(sz) : Root(Add(Sq(sx), Sq(sz)));
        Number length = Root(Add(Sq(horizontal), Sq(sy)));
        _sourceY = sy; _horizontal = horizontal; _length = length;
        _u = Div(horizontal, length); _k = Div(sy, length);
        _axisX = Div(sx, length); _axisZ = Div(sz, length);
        _axis = new(B(_axisX), NormalizedYBounds(), B(_axisZ));
        Number ex, ez;
        if (Sign(sz) == GeometrySign.Zero) { ex = C(Sign(sx) == GeometrySign.Positive ? 1 : -1); ez = C(0); }
        else if (Sign(sx) == GeometrySign.Zero) { ex = C(0); ez = C(Sign(sz) == GeometrySign.Positive ? 1 : -1); }
        else { ex = Div(sx, horizontal); ez = Div(sz, horizontal); }
        _eX = ex; _eZ = ez;
        _e = new(B(ex), I(0), B(ez)); _f = new(B(Neg(ez)), I(0), B(ex));
        _w = new(B(Neg(Mul(_k, ex))), B(_u), B(Neg(Mul(_k, ez))));
        _centre = leaf.Centre;
        Number dx = Sub(C(leaf.Centre.X.Lower), C(x)), dz = Sub(C(leaf.Centre.Z.Lower), C(z));
        _p = Add(Mul(dx, ex), Mul(dz, ez));
        _q = Add(Mul(dx, Neg(ez)), Mul(dz, ex));
        _r = C(leaf.Radius); _h = C(leaf.HalfLength); _disc = C(disc);
        return _axis.IsResolved && _w.IsResolved && B(_u).Lower > 0;
    }

    bool Keep(GeometryInterval z, double end = 0, bool atEnd = false, bool slope = false, int rim = 0,
        DiscLateralBoundary lateral = DiscLateralBoundary.None)
    {
        if (!z.IsResolved || z.Lower > 1 || z.Upper < -1) return true;
        if (_count == _candidates.Length) return false;
        // A tagged quotient must prove its original source domain before any clipping.
        _candidates[_count++] = new(lateral == DiscLateralBoundary.None ? Clip(z) : z, end, atEnd, slope, rim, lateral);
        return true;
    }

    static GeometryInterval Clip(GeometryInterval z) => GeometryInterval.Enclose(Math.Max(-1, z.Lower), Math.Min(1, z.Upper));

    bool Boundaries(double end, int rim = 0)
    {
        Number p = Add(_p, Mul(_u, C(end)));
        _boundaryP = p; _boundaryRim = rim < 0 ? -1 : 1;
        _boundaryB = Mul(C(2), Mul(_r, _q)); _boundaryJ = Mul(Sq(_r), Sq(_u));
        _boundaryK = Mul(C(rim < 0 ? -2 : 2), Mul(Mul(_r, _k), p));
        _boundaryMode = true;
        _axisMode = false;
        _stationaryMode = false;
        if (!SolveRoots(-1, 1, maximumRoots: 4)) return false;
        for (int i = 0; i < _roots.Count; i++)
        {
            GeometryInterval z = _roots.Get(i);
            if (!Keep(z, end, atEnd: true, rim: rim)) return false;
        }
        return true;
    }

    bool Stationary()
    {
        if (Sign(_q) == GeometrySign.Zero) return Keep(I(0));
        if (_axis.Y.Lower <= 0) return SignedStationary();
        Number k2 = Sq(_k), q2 = Sq(_q), r2 = Sq(_r), u2 = Sq(_u);
        _polynomial[0] = Neg(Mul(k2, q2));
        _polynomial[1] = Neg(Mul(C(2), Mul(k2, Mul(_q, _r))));
        _polynomial[2] = Add(Sub(Sub(Sq(_disc), q2), Mul(k2, r2)), Mul(k2, q2));
        _polynomial[3] = Neg(Mul(C(2), Mul(Mul(_q, _r), u2)));
        _polynomial[4] = Neg(Mul(r2, u2));
        // The original derivative requires z and Q+R*z to have opposite signs.
        // Its only possible root is between zero and -Q/R. This excludes the
        // squared equation's extraneous near-pole roots before subdivision.
        GeometryInterval other = B(Div(Neg(_q), _r));
        double low = Math.Max(-1, Math.Min(0, other.Lower)), high = Math.Min(1, Math.Max(0, other.Upper));
        if (!SolveRoots(_polynomial, low, high)) return false;
        for (int i = 0; i < _roots.Count; i++)
        {
            GeometryInterval z = _roots.Get(i), lateral = B(_q).Add(B(_r).Multiply(z));
            if (z.Lower > 0 && lateral.Lower > 0 || z.Upper < 0 && lateral.Upper < 0) continue;
            if ((z.Lower <= 0 && z.Upper >= 0) || (lateral.Lower <= 0 && lateral.Upper >= 0)) return false;
            if (!Keep(z)) return false;
        }
        return true;
    }

    // With equations false, both disc-lateral endpoints are evaluated as untagged candidates.
    internal GeometrySign Side(double slope, out Witness witness, out GeometryInterval maximum, bool equations = true)
    {
        witness = default; maximum = default;
        _count = 0;
        if (slope > 0)
        {
            GeometrySign available = Sign(Sub(_u, C(slope)));
            if (available == GeometrySign.Negative) return GeometrySign.Negative;
            if (available == GeometrySign.Unresolved)
            {
                _polynomial[0] = Sub(Sq(C(slope)), Sq(_u));
                _polynomial[1] = C(0); _polynomial[2] = Sq(_u);
                if (!SolveRoots(_polynomial.AsSpan(0, 3), -1, 1)) return GeometrySign.Unresolved;
                if (_roots.Count == 0) return GeometrySign.Negative;
                for (int i = 0; i < _roots.Count; i++)
                    if (!Keep(_roots.Get(i), slope: true)) return GeometrySign.Unresolved;
            }
            else
            {
                Number limit = Root(Sub(C(1), Sq(Div(C(slope), _u))));
                if (!Keep(B(limit), slope: true) || !Keep(B(Neg(limit)), slope: true)) return GeometrySign.Unresolved;
            }
        }
        if (!Keep(I(-1)) || !Keep(I(1)) || !Keep(I(0)) ||
            !Keep(B(Div(Sub(Neg(_disc), _q), _r)), lateral: equations ? DiscLateralBoundary.Negative : default) ||
            !Keep(B(Div(Sub(_disc, _q), _r)), lateral: equations ? DiscLateralBoundary.Positive : default) ||
            !Stationary()) return GeometrySign.Unresolved;
        if (Sign(_q) == GeometrySign.Zero &&
            SidePoint(new(I(0), 0, false, false, 0), slope, out Witness crest, out bool crestCertain) == GeometrySign.Positive && crestCertain)
        {
            // Both height branches decrease away from zero for every source when Q is zero.
            // This attained point covers all feasible components without isolating irrelevant end crossings.
            witness = crest; maximum = crest.Position.Y;
            return GeometrySign.Positive;
        }
        double attained = double.NegativeInfinity;
        for (int i = 0; i < _count; i++)
            if (SidePoint(_candidates[i], slope, out Witness offered, out bool certain) == GeometrySign.Positive && certain)
                attained = Math.Max(attained, offered.Position.Y.Lower);
        for (int direction = -1; direction <= 1; direction += 2)
        {
            double end = direction * B(_h).Lower;
            GeometryInterval endUpper = _centre.Y.Add(_axis.Y.Multiply(I(end))).Add(B(_r).Multiply(B(_u)));
            // Even a multiple or source-dependent crossing cannot improve this attained lower bound.
            if (endUpper.Upper < attained) continue;
            if (!Boundaries(end)) return GeometrySign.Unresolved;
        }
        bool any = false, possible = false;
        double lower = double.NegativeInfinity;
        // First establish an attained lower bound. Potential candidates never establish that bound.
        for (int i = 0; i < _count; i++)
        {
            GeometrySign feasible = SidePoint(_candidates[i], slope, out Witness point, out bool certain);
            if (feasible == GeometrySign.Unresolved) return feasible;
            if (feasible == GeometrySign.Positive)
            {
                possible = true;
                if (certain) lower = Math.Max(lower, point.Position.Y.Lower);
            }
        }
        if (!possible) return GeometrySign.Negative;
        if (!double.IsFinite(lower)) return GeometrySign.Unresolved;
        double upper = lower;
        for (int i = 0; i < _count; i++)
        {
            GeometrySign feasible = SidePoint(_candidates[i], slope, out Witness point, out _);
            if (feasible == GeometrySign.Unresolved) return feasible;
            if (feasible != GeometrySign.Positive || point.Position.Y.Upper < lower) continue;
            witness = any ? new(Hull(witness.Position, point.Position), Hull(witness.Normal, point.Normal),
                witness.AtSideCrest && point.AtSideCrest) : point;
            any = true; upper = Math.Max(upper, point.Position.Y.Upper);
        }
        maximum = GeometryInterval.Enclose(lower, upper);
        return any ? GeometrySign.Positive : GeometrySign.Unresolved;
    }

    GeometrySign SidePoint(Candidate candidate, double slope, out Witness point, out bool certain)
    {
        if (candidate.Lateral != DiscLateralBoundary.None)
        {
            GeometrySign proved = SideBoundary(candidate.Lateral == DiscLateralBoundary.Positive ? 1 : -1, slope, out point);
            certain = proved == GeometrySign.Positive;
            if (proved != GeometrySign.Unresolved) return proved;
            // An unproved equation falls back to the clipped untagged candidate, with exactly its own certainty.
            return SidePoint(new(Clip(candidate.Parameter), candidate.End, candidate.AtEnd, candidate.Slope,
                candidate.Rim), slope, out point, out certain);
        }
        point = default; certain = false;
        GeometryInterval z = candidate.Parameter, v = z.Lower == z.Upper && z.Lower == 0 ? I(1) :
            z.Lower == z.Upper && (z.Lower == -1 || z.Lower == 1) ? I(0) : PositiveRoot(I(1).Subtract(z.Square()));
        GeometryInterval normalY = B(_u).Multiply(v);
        if (!v.IsResolved || normalY.Upper < slope) return GeometrySign.Negative;
        if (candidate.Slope) { v = I(slope).Divide(B(_u)); normalY = I(slope); }
        GeometryInterval lateral = B(_q).Add(B(_r).Multiply(z));
        GeometryInterval square = B(_disc).Square().Subtract(lateral.Square());
        if (!square.IsResolved || square.Upper < 0) return GeometrySign.Negative;
        GeometryInterval extent = PositiveRoot(square);
        GeometryInterval centre = B(_r).Multiply(_axis.Y).Multiply(v).Subtract(B(_p));
        GeometryInterval low = centre.Subtract(extent).Divide(B(_u)), high = centre.Add(extent).Divide(B(_u));
        double half = B(_h).Lower;
        if (low.Lower > half || high.Upper < -half) return GeometrySign.Negative;
        GeometryInterval t;
        if (candidate.AtEnd)
        {
            t = I(candidate.End);
            certain = normalY.Lower >= slope;
        }
        else
        {
            GeometryInterval upper = GeometryInterval.Enclose(Math.Min(half, high.Lower), Math.Min(half, high.Upper));
            GeometryInterval lower = GeometryInterval.Enclose(Math.Max(-half, low.Lower), Math.Max(-half, low.Upper));
            t = _axis.Y.Lower >= 0 ? upper : _axis.Y.Upper <= 0 ? lower : Hull(lower, upper);
            certain = square.Lower >= 0 && low.Upper <= half && high.Lower >= -half && normalY.Lower >= slope;
        }
        GeometryVector normal = Add(Scale(_w, v), Scale(_f, z));
        if (slope > 0) normalY = GeometryInterval.Enclose(Math.Max(slope, normalY.Lower), normalY.Upper);
        normal = new(normal.X, normalY, normal.Z);
        point = new(Add(Add(_centre, Scale(_axis, t)), Scale(normal, B(_r))), normal, z.Lower == 0 && z.Upper == 0);
        return point.Position.IsResolved && normal.IsResolved ? GeometrySign.Positive : GeometrySign.Unresolved;
    }

    /// <summary>Certifies the side endpoint where Q+R*z equals sign*disc, after Begin. Positive is attained, Negative
    /// is proved absent and Unresolved leaves the source equation unproved.</summary>
    internal GeometrySign SideBoundary(int sign, double slope, out Witness point)
    {
        int checkpoint = _arithmetic.Checkpoint;
        try { return SideLateralPoint(sign > 0, slope, out point); }
        finally { _arithmetic.Restore(checkpoint); }
    }

    GeometrySign SideLateralPoint(bool positive, double slope, out Witness point)
    {
        point = default;
        Number numerator = Sub(positive ? _disc : Neg(_disc), _q);
        GeometrySign left = Sign(Add(numerator, _r)), right = Sign(Sub(_r, numerator));
        if (left == GeometrySign.Negative || right == GeometrySign.Negative) return GeometrySign.Negative;
        if (left == GeometrySign.Unresolved || right == GeometrySign.Unresolved) return GeometrySign.Unresolved;
        Number parameter = Div(numerator, _r);
        Number vertical = left == GeometrySign.Zero || right == GeometrySign.Zero ? C(0) : Root(Sub(C(1), Sq(parameter)));
        Number upward = Mul(_u, vertical);
        GeometrySign walkable = Sign(Sub(upward, C(slope)));
        if (walkable is GeometrySign.Negative or GeometrySign.Unresolved) return walkable;
        // Q+R*z is the signed disc radius by construction. Its transverse extent is zero.
        // Clear the positive u denominator to certify this unique axial point for every source.
        Number offset = Sub(Mul(Mul(_r, _k), vertical), _p), half = Mul(_u, _h);
        GeometrySign low = Sign(Add(offset, half)), high = Sign(Sub(half, offset));
        if (low == GeometrySign.Negative || high == GeometrySign.Negative) return GeometrySign.Negative;
        if (low == GeometrySign.Unresolved || high == GeometrySign.Unresolved) return GeometrySign.Unresolved;
        GeometryInterval z = B(parameter), v = B(vertical), t = B(Div(offset, _u));
        GeometryVector normal = Add(Scale(_w, v), Scale(_f, z));
        normal = new(normal.X, walkable == GeometrySign.Zero ? I(slope) :
            GeometryInterval.Enclose(Math.Max(slope, B(upward).Lower), B(upward).Upper), normal.Z);
        point = new(Add(Add(_centre, Scale(_axis, t)), Scale(normal, B(_r))), normal);
        return point.Position.IsResolved && normal.IsResolved ? GeometrySign.Positive : GeometrySign.Unresolved;
    }
}
