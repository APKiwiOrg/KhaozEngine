using System;
using System.Collections.Generic;
using System.Numerics;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Physics.Bepu;

/// <summary>A vertex moving against the margin-rounded shoulder or head rim. The shared capsule
/// rim equation supplies its quartic. Original radical domains certify both common normal cones.</summary>
internal static class ConvexCircularVertexSweep
{
    internal static bool TryEvaluate(in ConvexQueryRequest request, in InstalledSweepLeaf leaf, P a, int id,
        ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexLeafEvidence evidence)
    {
        evidence = default;
        if (request.Shape.Margin == 0) return false;
        int checkpoint = scratch.Polynomials.Used;
        double width = scratch.MaximumFractionWidth;
        try
        {
            // Projection is nonexpansive. Normalization adds at most 2/m to its Lipschitz
            // bound. Reserve the existing quarter budget for each encoded witness and normal.
            scratch.MaximumFractionWidth = width * request.Shape.Margin / 4;
            foreach (float height in new[] { request.Shape.RingY, request.Shape.UpperY })
            {
                if (!work.Feature() || !work.Cell()) return false;
                if (TryCircle(request, leaf, a, height, id, ref work, scratch, out evidence)) return true;
                if (work.Exhausted) return false;
                scratch.Polynomials.Restore(checkpoint);
            }
            return false;
        }
        finally { scratch.Polynomials.Restore(checkpoint); scratch.MaximumFractionWidth = width; }
    }

    static bool TryCircle(in ConvexQueryRequest request, in InstalledSweepLeaf leaf, P a, float height, int id,
        ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexLeafEvidence evidence)
    {
        evidence = default;
        var arena = scratch.Polynomials;
        ConvexPolynomial C(Q value) => value.Rational ? arena.Constant(value.A) : throw new ConvexExactRefusalException();
        ConvexPolynomial Line(Q origin, Q speed)
        {
            if (!origin.Rational || !speed.Rational) throw new ConvexExactRefusalException();
            if (speed.Sign == 0) return C(origin);
            var result = arena.Create(2);
            result.Set(0, origin.A); result.Set(1, speed.A);
            return result;
        }
        P d = P.From(request.Displacement);
        var x = Line(a.X, -d.X); var y = Line(a.Y - Q.From(height), -d.Y); var z = Line(a.Z, -d.Z);
        var rho2 = x.Multiply(x).Subtract(z.Multiply(z).Scale(-ConvexRational.One));
        Q radius = Q.From(request.Shape.Radius), margin = Q.From(request.Shape.Margin);
        var r2 = C(radius * radius);
        var arithmetic = new CircularPolynomialArithmetic(arena);
        var difference = CircularContactEquation.Difference(rho2, r2, y.Multiply(y), C(margin * margin), arithmetic);
        var equation = CircularContactEquation.SquaredDifference(difference, r2, rho2, arithmetic);
        bool rim = height == request.Shape.UpperY;
        var radicals = new List<ConvexRadicalPredicate>();
        if (!rim)
        {
            // h*ny + R*(rho-R) >= 0 is the shoulder's closed cone boundary.
            var k = y.Scale((Q.From(request.Shape.RingY) - Q.From(request.Shape.LowerY)).A).Subtract(r2);
            radicals.Add(new(k, C(radius), rho2));
        }
        for (int vertex = 0; vertex < leaf.Geometry.VertexCount; vertex++)
        {
            if (!work.Feature()) return false;
            P offset = ConvexPlanarFeatures.Vertex(leaf, vertex, request.Pose.Position) - a;
            var horizontal = x.Scale(offset.X.A).Subtract(z.Scale(-offset.Z.A));
            var full = horizontal.Subtract(y.Scale(-offset.Y.A));
            // Multiply n.offset by positive rho, retaining the original radical sign.
            radicals.Add(new(horizontal.Scale(-radius.A), full, rho2));
        }
        var regime = new ConvexFeatureRegime(equation,
            [rho2.Subtract(r2), difference, rim ? y : y.Scale(-ConvexRational.One)], radicals.ToArray());
        if (!ConvexAlgebraicRoots.TryIsolate(regime, 0, 1, ref work, scratch, out var root) || !root.HasRoot) return false;
        return TryPublish(request, leaf, a, height, id, root, ref work, out evidence);
    }

    static bool TryPublish(in ConvexQueryRequest request, in InstalledSweepLeaf leaf, P a, float height, int id,
        ConvexRootBracket root, ref ConvexQueryWork work, out ConvexLeafEvidence evidence)
    {
        evidence = default;
        GeometryInterval time = GeometryInterval.Enclose(root.Lower, root.Upper);
        var aa = a.Bounds; var d = P.From(request.Displacement).Bounds;
        var px = aa.X.Subtract(d.X.Multiply(time));
        var py = aa.Y.Subtract(d.Y.Multiply(time)).Subtract(I(height));
        var pz = aa.Z.Subtract(d.Z.Multiply(time));
        CircularProjection.Normal<GeometryInterval, CircularIntervalArithmetic>(px, py, pz, I(0), I(1), I(0),
            I(request.Shape.Radius), I(0), out var nx, out var ny, out var nz, out _);
        GeometryVector n = new(nx, ny, nz);
        var speed = Dot(n, d);
        // At the certified root n supports both complete solids. Positive speed proves every
        // earlier original-path point separated by (tau-t)*n.d, without inspecting samples.
        if (!n.IsResolved || !speed.IsResolved || (root.Upper != 0 && speed.Lower <= 0)) return false;
        var point = (a + P.From(request.Pose.Position)).Bounds;
        if (!ConvexSweepEncoding.TryEnclosedHit(request, root.Lower, root.Upper, point, n,
            height == request.Shape.UpperY ? ConvexShellFeature.Rim : ConvexShellFeature.Shoulder,
            ConvexTargetFeature.Vertex, id, out var candidate)) return false;
        if (root.Upper != 0)
        {
            float advance = MathF.Max(0, candidate.UpperDistance - 0.001f);
            Vector3 placed = request.Pose.Position;
            if (advance > 0) placed += Vector3.Normalize(request.Displacement) * advance;
            if (!float.IsFinite(placed.X) || !float.IsFinite(placed.Y) || !float.IsFinite(placed.Z)) return false;
            var shift = (P.From(placed) - P.From(request.Pose.Position)).Bounds;
            GeometryVector q = new(px.Subtract(nx), I(height), pz.Subtract(nz));
            var norm = nx.Square().Add(ny.Square()).Add(nz.Square()).Sqrt();
            for (int vertex = 0; vertex < leaf.Geometry.VertexCount; vertex++)
            {
                if (!work.Feature()) return false;
                var v = ConvexPlanarFeatures.Vertex(leaf, vertex, request.Pose.Position).Bounds;
                GeometryVector offset = new(v.X.Subtract(q.X).Subtract(shift.X),
                    v.Y.Subtract(q.Y).Subtract(shift.Y), v.Z.Subtract(q.Z).Subtract(shift.Z));
                var gap = Dot(offset, n).Subtract(norm.Multiply(I(request.Shape.Margin)));
                if (!gap.IsResolved || gap.Lower <= 0) return false;
            }
        }
        evidence = candidate;
        return true;
    }

    static GeometryInterval I(double value) => GeometryInterval.Exact(value);
    static GeometryInterval Dot(GeometryVector a, GeometryVector b) =>
        a.X.Multiply(b.X).Add(a.Y.Multiply(b.Y)).Add(a.Z.Multiply(b.Z));
}
