using System;
using System.Numerics;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Closed planar contact seeds never authorize a miss. A successful contact also needs
/// a dual proof of its whole earlier path. Unsupported seeds and undecided paths refuse atomically.</summary>
internal static class ConvexPlanarSweep
{
    internal static bool TryEvaluate(in ConvexQueryRequest request, in InstalledSweepLeaf leaf,
        ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexLeafEvidence evidence)
    {
        evidence = default;
        GeometryInterval length = ConvexSweepEncoding.Length(request.Displacement);
        if (!length.IsResolved) return false;
        double savedWidth = scratch.MaximumFractionWidth;
        int checkpoint = scratch.Polynomials.Used;
        try
        {
            scratch.MaximumFractionWidth = length.Upper == 0 ? 1 : Math.Min(1, 0.001f / (4d * length.Upper));
            P d = P.From(request.Displacement);
            var g = leaf.Geometry;
            for (int face = 0; face < g.FaceCount; face++)
            {
                if (!work.Feature() || !work.Cell()) return false;
                P a = ConvexPlanarFeatures.Vertex(leaf, g.FaceVertex(face, 0), request.Pose.Position);
                P b = ConvexPlanarFeatures.Vertex(leaf, g.FaceVertex(face, 1), request.Pose.Position);
                P c = ConvexPlanarFeatures.Vertex(leaf, g.FaceVertex(face, g.InteriorFaceSize(face) - 1), request.Pose.Position);
                P n = P.Cross(c - a, b - a);
                if (Separated(request, leaf, n, 1, ref work)) return ConvexSweepEncoding.TryClear(request, out evidence);
                P q = ConvexFeatureKernel.Support(request.Shape, n);
                Q norm = Q.Root(P.Dot(n, n).A);
                if (!TryGrow(q, n, norm, request.Shape.Margin, out P grown)) continue;
                Q gap = P.Dot(a - grown, n), speed = P.Dot(d, n);
                Q time = gap.Sign == 0 ? default : speed.Sign > 0 ? gap / speed : Q.From(-1);
                if (!Closed(time)) continue;
                P t = grown + d * time;
                if (!ConvexPlanarFeatures.OnFace(leaf, face, request.Pose.Position, t)) continue;
                if (TryHit(request, leaf, time, q, grown, n, ConvexTargetFeature.Face, face, ref work, scratch, out evidence)) return true;
                if (work.Exhausted) return false;
            }
            // A zero path still needs attained closed vertex and edge boundary certificates.
            for (int vertex = 0; vertex < g.VertexCount; vertex++)
            {
                P a = ConvexPlanarFeatures.Vertex(leaf, vertex, request.Pose.Position);
                if (ConvexSweepVertices.TryEvaluate(request, leaf, a, g.FaceCount + g.EdgeCount + vertex,
                    ref work, scratch, out evidence)) return true;
                if (work.Exhausted) return false;
                if (ConvexCircularVertexSweep.TryEvaluate(request, leaf, a, g.FaceCount + g.EdgeCount + vertex,
                    ref work, scratch, out evidence)) return true;
                if (work.Exhausted) return false;
            }
            for (int edge = 0; edge < g.EdgeCount; edge++)
            {
                var ends = g.Edge(edge);
                P a = ConvexPlanarFeatures.Vertex(leaf, ends.A, request.Pose.Position);
                P v = ConvexPlanarFeatures.Vertex(leaf, ends.B, request.Pose.Position) - a;
                if (ConvexSweepEdges.TryEvaluate(request, leaf, a, v, g.FaceCount + edge,
                    ref work, scratch, out evidence)) return true;
                if (work.Exhausted) return false;
            }
            // Distance is a direction proposal only. The exact complete support function and
            // every represented target vertex independently certify the entire original path.
            if (TryPrefix(request, leaf, 1, ref work, scratch)) return ConvexSweepEncoding.TryClear(request, out evidence);
            evidence = default;
            return false;
        }
        finally { scratch.Polynomials.Restore(checkpoint); scratch.MaximumFractionWidth = savedWidth; }
    }

    internal static bool Closed(Q t) => t.Sign >= 0 && (Q.From(1) - t).Sign >= 0;

    static bool TryGrow(P q, P n, Q norm, float margin, out P grown)
    {
        grown = q;
        if (norm.Sign <= 0) return false;
        if (margin == 0) return true;
        // Mixed quadratic extensions are not rounded into one field. They remain unsupported.
        foreach (Q component in new[] { q.X, q.Y, q.Z })
            if (!component.Rational && !norm.Rational && component.D.CompareTo(norm.D) != 0) return false;
        grown = q + n * (Q.From(margin) / norm);
        return true;
    }

    internal static bool TryHit(in ConvexQueryRequest request, in InstalledSweepLeaf leaf, Q time,
        P q, P target, P normal, ConvexTargetFeature feature, int id, ref ConvexQueryWork work,
        ConvexQueryScratch scratch, out ConvexLeafEvidence evidence)
    {
        evidence = default;
        if (!Closed(time) || !work.Feature() || !work.Cell() || !ConvexPlanarFeatures.Member(request.Shape, q) ||
            !ConvexPlanarFeatures.NormalCone(request.Shape, q, normal) || P.Dot(normal, normal).Sign <= 0 ||
            !ConvexPlanarFeatures.SupportsTarget(leaf, request.Pose.Position, target + P.From(request.Displacement) * time, normal, ref work)) return false;
        P offset = target - q;
        Q square = P.Dot(offset, offset), margin = Q.From(request.Shape.Margin);
        if ((square - margin * margin).Sign != 0 || P.Dot(offset, normal).Sign < 0 ||
            P.Dot(P.Cross(offset, normal), P.Cross(offset, normal)).Sign != 0) return false;
        // Retain the original radical equation through both closed sign predicates. The
        // committed square-free/Sturm solver, not a sign crossing, isolates repeated roots.
        int checkpoint = scratch.Polynomials.Used;
        try
        {
            var arena = scratch.Polynomials;
            var k = arena.Create(2); k.Set(0, -time.A); k.Set(1, ConvexRational.One);
            var l = arena.Constant(-time.B);
            var radicand = arena.Constant(time.D);
            var equation = k.Multiply(k).Subtract(l.Multiply(l).Multiply(radicand));
            var zero = new ConvexRadicalPredicate(k, l, radicand);
            var negative = new ConvexRadicalPredicate(k.Scale(-ConvexRational.One), l.Scale(-ConvexRational.One), radicand);
            var regime = new ConvexFeatureRegime(equation, RadicalDomains: [zero, negative]);
            if (!ConvexAlgebraicRoots.TryIsolate(regime, 0, 1, ref work, scratch, out var root) || !root.HasRoot) return false;
            var exactTime = time.Bounds;
            if (!exactTime.IsResolved) return false;
            double lower = Math.Max(root.Lower, Math.Max(0, exactTime.Lower));
            double upper = Math.Min(root.Upper, Math.Min(1, exactTime.Upper));
            if (lower > upper) return false;
            Q speed = P.Dot(normal, P.From(request.Displacement));
            P safeNormal = normal;
            Q safeSupport = P.Dot(q, normal);
            if (time.Sign != 0 && speed.Sign <= 0)
            {
                // A tangent's supporting plane may contain the whole path. An earlier strict
                // separating plane must prove the prefix, including the exact dyadic case.
                lower = Math.Max(0, lower - scratch.MaximumFractionWidth * 0.5);
                P shifted = normal + P.From(request.Displacement) * (time - Q.From(lower));
                var bounds = shifted.Bounds;
                if (!bounds.IsResolved) return false;
                Vector3 proposal = new((float)(bounds.X.Lower * 0.5 + bounds.X.Upper * 0.5),
                    (float)(bounds.Y.Lower * 0.5 + bounds.Y.Upper * 0.5), (float)(bounds.Z.Lower * 0.5 + bounds.Z.Upper * 0.5));
                safeNormal = P.From(proposal);
                if (!Separated(request, leaf, safeNormal, lower, ref work)) return false;
                safeSupport = P.Dot(ConvexFeatureKernel.Support(request.Shape, safeNormal), safeNormal);
            }
            if (!ConvexSweepEncoding.TryHit(request, time, lower, upper, target, normal,
                ConvexPlanarFeatures.Classify(request.Shape, q), feature, id, out var candidate)) return false;
            if (time.Sign != 0)
            {
                float advance = MathF.Max(0, candidate.UpperDistance - 0.001f);
                Vector3 placed = request.Pose.Position;
                if (advance > 0) placed += Vector3.Normalize(request.Displacement) * advance;
                if (!float.IsFinite(placed.X) || !float.IsFinite(placed.Y) || !float.IsFinite(placed.Z) ||
                    !SeparatedOffset(request, leaf, safeNormal, safeSupport,
                        P.From(placed) - P.From(request.Pose.Position), ref work)) return false;
            }
            evidence = candidate;
            return true;
        }
        finally { scratch.Polynomials.Restore(checkpoint); }
    }

    static bool TryPrefix(in ConvexQueryRequest request, in InstalledSweepLeaf leaf, double to,
        ref ConvexQueryWork work, ConvexQueryScratch scratch)
    {
        var pose = Pose.At(request.Pose.Position + request.Displacement * (float)to);
        var proposal = request with { Pose = pose, Displacement = Vector3.Zero, Operation = ConvexQueryOperation.Distance };
        if (!ConvexFeatureKernel.TryDistance(proposal, leaf, ref work, scratch, out var distance) ||
            distance.Normal == Vector3.Zero || work.Exhausted) return false;
        return Separated(request, leaf, P.From(-distance.Normal), to, ref work);
    }

    internal static bool Separated(in ConvexQueryRequest request, in InstalledSweepLeaf leaf, P n, double to,
        ref ConvexQueryWork work)
    {
        if (!n.X.Rational || !n.Y.Rational || !n.Z.Rational || P.Dot(n, n).Sign <= 0) return false;
        Q speed = P.Dot(P.From(request.Displacement), n);
        P offset = speed.Sign > 0 ? P.From(request.Displacement) * Q.From(to) : default;
        return SeparatedOffset(request, leaf, n, P.Dot(ConvexFeatureKernel.Support(request.Shape, n), n), offset, ref work);
    }

    static bool SeparatedOffset(in ConvexQueryRequest request, in InstalledSweepLeaf leaf, P n, Q support,
        P offset, ref ConvexQueryWork work)
    {
        if (!work.Feature()) return false;
        Q norm2 = P.Dot(n, n);
        if (norm2.Sign <= 0) return false;
        Q minimum = P.Dot(ConvexPlanarFeatures.Vertex(leaf, 0, request.Pose.Position), n);
        for (int i = 1; i < leaf.Geometry.VertexCount; i++)
        {
            if (!work.Feature()) return false;
            Q projection = P.Dot(ConvexPlanarFeatures.Vertex(leaf, i, request.Pose.Position), n);
            if ((projection - minimum).Sign < 0) minimum = projection;
        }
        Q gap = minimum - support - P.Dot(offset, n);
        var separation = gap.Bounds.Subtract(Q.RootBounds(norm2).Multiply(GeometryInterval.Exact(request.Shape.Margin)));
        return separation.IsResolved && separation.Lower > 0;
    }
}
