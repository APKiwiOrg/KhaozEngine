using System;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Physics.Bepu;

/// <summary>An attained circular pair at the original unsquared stationary root. Polynomial
/// signs refer to that same root, including exact margin ties and original-target revalidation.</summary>
internal sealed class ConvexCircularCertificate
{
    internal ConvexAlgebraicRoots.Retained Root { get; }
    internal ConvexDistanceContext Context { get; }
    readonly P _a, _v;
    readonly Q _radius, _height;
    readonly ConvexPolynomial _x, _y, _z, _rho2;
    internal ConvexShellFeature Feature { get; }
    internal int LocalFeatureId { get; }
    ConvexCircularCertificate(ConvexAlgebraicRoots.Retained root, in ConvexDistanceContext context, P a, P v,
        Q radius, Q height, ConvexPolynomial x, ConvexPolynomial y, ConvexPolynomial z, ConvexPolynomial rho2, int id)
    {
        Root = root; Context = context; _a = a; _v = v; _radius = radius; _height = height;
        _x = x; _y = y; _z = z; _rho2 = rho2; LocalFeatureId = id;
        Feature = (height - Q.From(context.Request.Shape.UpperY)).Sign == 0 ? ConvexShellFeature.Rim : ConvexShellFeature.Shoulder;
    }

    internal static bool TryCreate(in ConvexDistanceContext context, P a, P v, Q radius, Q height,
        GeometryInterval bracket, int id, ref ConvexQueryWork work, out ConvexCircularCertificate certificate)
    {
        certificate = null!;
        var scratch = context.Scratch;
        int checkpoint = scratch.Polynomials.Used;
        bool retained = false;
        try
        {
            if (!work.Feature()) return false;
            var arena = scratch.Polynomials;
            var x = Line(arena, a.X, v.X); var y = Line(arena, a.Y - height, v.Y); var z = Line(arena, a.Z, v.Z);
            var rho2 = Add(x.Multiply(x), z.Multiply(z));
            var radialDot = Add(x.Scale(v.X.A), z.Scale(v.Z.A));
            var fullDot = Add(radialDot, y.Scale(v.Y.A));
            var equation = rho2.Multiply(fullDot.Multiply(fullDot)).Subtract(radialDot.Multiply(radialDot).Scale((radius * radius).A));
            if (!ConvexAlgebraicRoots.TryRetain(equation, bracket.Lower, bracket.Upper, ref work, scratch, out var root) ||
                !root.TryRadicalSign(new(radialDot.Scale(-radius.A), fullDot, rho2), ref work, scratch, out int stationary) || stationary != 0) return false;
            certificate = new(root, context, a, v, radius, height, x, y, z, rho2, id);
            retained = true;
            return true;
        }
        finally { if (!retained) scratch.Polynomials.Restore(checkpoint); }
    }

    internal bool TryCompareSquaredMargin(float margin, ref ConvexQueryWork work, ConvexQueryScratch scratch, out int sign)
    {
        sign = 0;
        if (!Root.IsAlive || !ReferenceEquals(Root.Owner, scratch) || !float.IsFinite(margin) || margin < 0 || !work.Feature()) return false;
        if (margin == 0) { sign = 1; return true; } // The retained radial domain is strictly outside R.
        GeometryInterval squaredBounds = SquaredDistanceBounds, marginBounds = (Q.From(margin) * Q.From(margin)).Bounds;
        if (squaredBounds.IsResolved && marginBounds.IsResolved)
        {
            if (squaredBounds.Lower > marginBounds.Upper) { sign = 1; return true; }
            if (squaredBounds.Upper < marginBounds.Lower) { sign = -1; return true; }
        }
        return TryCompareSquared(Q.From(margin) * Q.From(margin), ref work, scratch, out sign);
    }

    internal bool TryCompareSquared(Q squaredValue, ref ConvexQueryWork work, ConvexQueryScratch scratch, out int sign)
    {
        sign = 0;
        if (!Root.IsAlive || !ReferenceEquals(Root.Owner, scratch) || !squaredValue.Rational || squaredValue.Sign < 0 || !work.Feature()) return false;
        int checkpoint = scratch.Polynomials.Used;
        try
        {
            var arena = scratch.Polynomials;
            var arithmetic = new CircularPolynomialArithmetic(arena);
            var r2 = arena.Constant((_radius * _radius).A);
            var difference = CircularContactEquation.Difference(_rho2, r2, _y.Multiply(_y), arena.Constant(squaredValue.A), arithmetic);
            if (!Root.TrySign(difference, ref work, scratch, out int original)) return false;
            if (original < 0) { sign = -1; return true; }
            var squared = CircularContactEquation.SquaredDifference(difference, r2, _rho2, arithmetic);
            return Root.TrySign(squared, ref work, scratch, out sign);
        }
        catch (ConvexExactRefusalException) { return work.Refuse(); }
        finally { scratch.Polynomials.Restore(checkpoint); }
    }

    internal bool TryTemporalSupport(in ConvexQueryRequest request, in ConvexPlanarSource source,
        double from, double to, ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexTemporalVertex target)
    {
        target = default;
        if (!Root.IsAlive || !ReferenceEquals(Root.Owner, scratch) || !work.Cell()) return false;
        int vertex = 0;
        for (int i = 1; i < source.VertexCount; i++)
        {
            if (!TryNormalDotSign(source.Vertex(i) - source.Vertex(vertex), ref work, scratch, out int sign)) return false;
            if (sign < 0) vertex = i;
        }
        if (!TryNormalDotSign(P.From(request.Displacement), ref work, scratch, out int velocity)) return false;
        target = ConvexTemporalVertex.Create(request, source, vertex, velocity > 0 ? to : from);
        return true;
    }

    bool TryNormalDotSign(P offset, ref ConvexQueryWork work, ConvexQueryScratch scratch, out int sign)
    {
        sign = 0;
        if (!offset.X.Rational || !offset.Y.Rational || !offset.Z.Rational || !work.Feature()) return false;
        int checkpoint = scratch.Polynomials.Used;
        try
        {
            var horizontal = Add(_x.Scale(offset.X.A), _z.Scale(offset.Z.A));
            var full = Add(horizontal, _y.Scale(offset.Y.A));
            return Root.TryRadicalSign(new(horizontal.Scale(-_radius.A), full, _rho2), ref work, scratch, out sign);
        }
        finally { scratch.Polynomials.Restore(checkpoint); }
    }

    internal GeometryVector TargetBounds => Point(Root.Bounds);
    internal GeometryVector NormalBounds
    {
        get
        {
            var t = TargetBounds;
            CircularProjection.Normal<GeometryInterval, CircularIntervalArithmetic>(t.X, t.Y.Subtract(_height.Bounds), t.Z,
                I(0), I(1), I(0), _radius.Bounds, I(0), out var x, out var y, out var z, out _);
            return new(x, y, z);
        }
    }
    internal GeometryVector QueryBounds
    {
        get { var t = TargetBounds; var n = NormalBounds; return new(t.X.Subtract(n.X), _height.Bounds, t.Z.Subtract(n.Z)); }
    }
    internal GeometryInterval SquaredDistanceBounds
    {
        get { var n = NormalBounds; return n.X.Square().Add(n.Y.Square()).Add(n.Z.Square()); }
    }
    GeometryVector Point(GeometryInterval s) => new(_a.X.Bounds.Add(_v.X.Bounds.Multiply(s)),
        _a.Y.Bounds.Add(_v.Y.Bounds.Multiply(s)), _a.Z.Bounds.Add(_v.Z.Bounds.Multiply(s)));

    internal bool TryAtFraction(in ConvexQueryRequest request, in ConvexPlanarSource source, ConvexCertificateFraction time,
        ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexDistanceCertificate certificate)
    {
        certificate = default;
        if (!Root.IsAlive || !ReferenceEquals(Root.Owner, scratch) || !ReferenceEquals(time.Root, Root) ||
            !source.IsValid || !request.Shape.Equals(Context.Request.Shape) || !work.Feature()) return false;
        // A constant path reuses the already closed source predicates at the same root.
        if (ReferenceEquals(source.Token, Context.Source.Token) && request.Pose.Position == Context.Request.Pose.Position &&
            request.Displacement == System.Numerics.Vector3.Zero && Context.Feet == P.From(request.Pose.Position))
        { certificate = new(request, source, time, scratch, this); return true; }
        if (!source.IsOriginal) return false;
        int checkpoint = scratch.Polynomials.Used;
        try
        {
            var arena = scratch.Polynomials;
            P p = P.From(request.Pose.Position), d = P.From(request.Displacement);
            P origin = _a + p + d * new Q(time.Offset), speed = _v + d * new Q(time.Scale);
            int closedFace = -1;
            // The translated attained point must be inside the complete original leaf.
            for (int face = 0; face < source.FaceCount; face++)
            {
                if (!work.Feature()) return false;
                P a = source.Vertex(source.FaceVertex(face, 0));
                P n = P.Cross(source.Vertex(source.FaceVertex(face, 1)) - a,
                    source.Vertex(source.FaceVertex(face, source.InteriorFaceSize(face) - 1)) - a);
                var predicate = Line(arena, P.Dot(n, origin - a), P.Dot(n, speed));
                if (!Root.TrySign(predicate, ref work, scratch, out int sign) || sign > 0) return false;
                if (sign == 0 && closedFace < 0) closedFace = face;
                arena.Restore(checkpoint);
            }
            if (closedFace < 0) return false;
            for (int i = 0; i < source.VertexCount; i++)
            {
                if (!work.Feature()) return false;
                P offset = source.Vertex(i) - origin;
                var ox = Line(arena, offset.X, -speed.X); var oy = Line(arena, offset.Y, -speed.Y); var oz = Line(arena, offset.Z, -speed.Z);
                var horizontal = Add(ox.Multiply(_x), oz.Multiply(_z));
                var full = Add(horizontal, oy.Multiply(_y));
                if (!Root.TryRadicalSign(new(horizontal.Scale(-_radius.A), full, _rho2), ref work, scratch, out int sign) || sign < 0) return false;
                arena.Restore(checkpoint);
            }
            certificate = new(request, source, time, scratch, this, ConvexTargetFeature.Face, closedFace);
            return true;
        }
        catch (ConvexExactRefusalException) { return work.Refuse(); }
        finally { scratch.Polynomials.Restore(checkpoint); }
    }

    static GeometryInterval I(double value) => GeometryInterval.Exact(value);
    static ConvexPolynomial Add(ConvexPolynomial a, ConvexPolynomial b) => a.Subtract(b.Scale(-ConvexRational.One));
    static ConvexPolynomial Line(ConvexPolynomialWorkspace arena, Q origin, Q speed)
    {
        if (!origin.Rational || !speed.Rational) throw new ConvexExactRefusalException();
        if (speed.Sign == 0) return arena.Constant(origin.A);
        var line = arena.Create(2); line.Set(0, origin.A); line.Set(1, speed.A); return line;
    }
}
