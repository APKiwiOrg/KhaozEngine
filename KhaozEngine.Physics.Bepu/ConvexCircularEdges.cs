using System;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Circle/edge KKT certificates using the capsule's disc projection and the support
/// footprint's source-uniform root regions. The root is existential, never a rounded sample.</summary>
internal sealed class ConvexCircularEdges : ISupportCurvedRootFunction
{
    readonly SupportCurvedWork _supportWork;
    readonly SupportCurvedRootRegions _roots;
    ConvexQueryWork _work;
    P _a, _v;
    Q _radius, _height;
    GeometryInterval _derivative;

    internal ConvexCircularEdges()
    {
        _supportWork = new(admit: kind => kind is SupportCurvedWorkKind.RootCell or SupportCurvedWorkKind.Refine
            ? _work.Cell() : _work.Feature());
        _roots = new(_supportWork, ConvexQueryPolicy.MaximumDepth + 1);
    }

    internal bool TryEvaluate(in ConvexDistanceContext context, P a, P v, int id,
        ref ConvexQueryWork work, out ConvexStaticResult result)
    {
        result = default;
        var request = context.Request;
        // Horizontal edges already have exact radial stationarity in ConvexPlanarEdges.
        // For oblique edges the disc projection is strongly monotone with modulus vy^2.
        if (v.Y.Sign == 0) return false;
        _work = work; _a = a; _v = v; _radius = Q.From(request.Shape.Radius);
        _derivative = GeometryInterval.Enclose((v.Y * v.Y).Bounds.Lower, P.Dot(v, v).Bounds.Upper);
        try
        {
            foreach (float height in new[] { request.Shape.RingY, request.Shape.UpperY })
            {
                _height = Q.From(height);
                _supportWork.Begin();
                if (!_roots.Solve(this, 0, 1, maximumRoots: 1)) return false;
                if (_roots.Count == 0) continue;
                GeometryInterval s = _roots.Get(0);
                if (s.IsResolved && s.Lower == s.Upper && ConvexFeatureKernel.TryPoint(context,
                    a + v * Q.From(s.Lower), ConvexTargetFeature.Edge, id, ref _work, out result)) return true;
                if (!s.IsResolved) continue;
                if (!context.Encode)
                {
                    if (!TryRetain(context, s, id, out var circular)) continue;
                    result = new(default, new(request, context.Source, context.Fraction, context.Scratch, circular));
                    return true;
                }
                if (!TryCertificate(request, context.Source.Leaf, s, id, out var evidence)) continue;
                result = new(evidence, default);
                return true;
            }
            return false;
        }
        finally { work = _work; }
    }

    bool TryRetain(in ConvexDistanceContext context, GeometryInterval s, int id, out ConvexCircularCertificate certificate)
    {
        certificate = null!;
        var request = context.Request;
        if (!_work.Feature()) return false;
        GeometryVector n = Normal(s, out var radial);
        if (!n.IsResolved || !radial.IsResolved || radial.Lower <= _radius.Bounds.Upper) return false;
        bool rim = (_height - Q.From(request.Shape.UpperY)).Sign == 0;
        if (rim ? n.Y.Lower < 0 : n.Y.Upper > 0) return false;
        if (!rim)
        {
            GeometryInterval cone = (Q.From(request.Shape.RingY) - Q.From(request.Shape.LowerY)).Bounds.Multiply(n.Y)
                .Add(_radius.Bounds.Multiply(radial.Subtract(_radius.Bounds)));
            if (!cone.IsResolved || cone.Lower < 0) return false;
        }
        for (int i = 0; i < context.Source.VertexCount; i++)
        {
            if (!_work.Feature()) return false;
            P offset = context.Source.Vertex(i, context.Feet) - _a;
            P cross = P.Cross(offset, _v);
            if (cross.X.Sign == 0 && cross.Y.Sign == 0 && cross.Z.Sign == 0) continue;
            GeometryInterval residual = Dot(offset.Bounds, n);
            if (!residual.IsResolved || residual.Lower < 0) return false;
        }
        return ConvexCircularCertificate.TryCreate(context, _a, _v, _radius, _height, s, id, ref _work, out certificate);
    }

    GeometryVector Point(GeometryInterval s) => new(_a.X.Bounds.Add(_v.X.Bounds.Multiply(s)),
        _a.Y.Bounds.Add(_v.Y.Bounds.Multiply(s)), _a.Z.Bounds.Add(_v.Z.Bounds.Multiply(s)));
    GeometryVector Normal(GeometryInterval s, out GeometryInterval radial)
    {
        var p = Point(s);
        CircularProjection.Normal<GeometryInterval, CircularIntervalArithmetic>(p.X, p.Y.Subtract(_height.Bounds), p.Z,
            I(0), I(1), I(0), _radius.Bounds, I(0), out var x, out var y, out var z, out radial);
        return new(x, y, z);
    }
    static GeometryInterval I(double x) => GeometryInterval.Exact(x);
    static GeometryInterval Dot(GeometryVector a, GeometryVector b) => a.X.Multiply(b.X).Add(a.Y.Multiply(b.Y)).Add(a.Z.Multiply(b.Z));

    GeometryInterval ISupportCurvedRootFunction.Evaluate(GeometryInterval s, bool derivative)
    {
        if (!_work.Feature()) return default;
        return derivative ? _derivative : Dot(Normal(s, out _), _v.Bounds);
    }
    GeometrySign ISupportCurvedRootFunction.DerivativeSign(GeometryInterval s) =>
        _work.Feature() && _derivative.IsResolved && _derivative.Lower > 0 ? GeometrySign.Positive : GeometrySign.Unresolved;
    GeometrySign ISupportCurvedRootFunction.Sign(double parameter)
    {
        if (!_work.Feature()) return GeometrySign.Unresolved;
        P p = _a + _v * Q.From(parameter);
        CircularProjection.Normal<Q, CircularQuadraticArithmetic>(p.X, p.Y - _height, p.Z,
            Q.From(0), Q.From(1), Q.From(0), _radius, Q.From(0), out var x, out var y, out var z, out _);
        return P.Dot(new(x, y, z), _v).Sign switch
        { < 0 => GeometrySign.Negative, > 0 => GeometrySign.Positive, _ => GeometrySign.Zero };
    }

    bool TryCertificate(in ConvexQueryRequest request, in InstalledSweepLeaf leaf, GeometryInterval s, int id,
        out ConvexLeafEvidence evidence)
    {
        evidence = default;
        if (!_work.Feature()) return false;
        GeometryVector t = Point(s), n = Normal(s, out var radial);
        if (!n.IsResolved || !radial.IsResolved || radial.Lower <= _radius.Bounds.Upper) return false;
        bool rim = (_height - Q.From(request.Shape.UpperY)).Sign == 0;
        if (rim ? n.Y.Lower < 0 : n.Y.Upper > 0) return false;
        if (!rim)
        {
            GeometryInterval cone = (Q.From(request.Shape.RingY) - Q.From(request.Shape.LowerY)).Bounds.Multiply(n.Y)
                .Add(_radius.Bounds.Multiply(radial.Subtract(_radius.Bounds)));
            if (!cone.IsResolved || cone.Lower < 0) return false;
        }
        // At this root n.v=0 by the original unsquared projection equation. Parallel
        // vertex offsets therefore have exact zero support residual, including both endpoints.
        for (int i = 0; i < leaf.Geometry.VertexCount; i++)
        {
            if (!_work.Feature()) return false;
            P offset = ConvexPlanarFeatures.Vertex(leaf, i, request.Pose.Position) - _a;
            P cross = P.Cross(offset, _v);
            if (cross.X.Sign == 0 && cross.Y.Sign == 0 && cross.Z.Sign == 0) continue;
            GeometryInterval residual = Dot(offset.Bounds, n);
            if (!residual.IsResolved || residual.Lower < 0) return false;
        }
        GeometryVector q = new(t.X.Subtract(n.X), _height.Bounds, t.Z.Subtract(n.Z));
        GeometryInterval length = n.X.Square().Add(n.Y.Square()).Add(n.Z.Square()).Sqrt();
        if (!length.IsResolved) return false;
        bool overlap = length.Upper <= request.Shape.Margin;
        if (!overlap && length.Lower <= request.Shape.Margin) return false;
        GeometryVector unit = overlap ? new(I(0), I(0), I(0)) : new(n.X.Divide(length), n.Y.Divide(length), n.Z.Divide(length));
        if (overlap) q = t;
        else if (request.Shape.Margin != 0)
        {
            GeometryInterval m = I(request.Shape.Margin);
            q = new(q.X.Add(unit.X.Multiply(m)), q.Y.Add(unit.Y.Multiply(m)), q.Z.Add(unit.Z.Multiply(m)));
        }
        return ConvexDistanceEncoding.TryEnclosed(request, q, t, unit, overlap ? I(0) : length.Subtract(I(request.Shape.Margin)),
            rim ? ConvexShellFeature.Rim : ConvexShellFeature.Shoulder, ConvexTargetFeature.Edge, id, overlap, out evidence);
    }
}
