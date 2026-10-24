using System.Numerics;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Planar distance certificates. A closed primal pair plus a common supporting
/// normal proves global optimality by convexity. Candidate failure never proves separation.</summary>
internal static class ConvexFeatureKernel
{
    internal static bool TryEvaluate(in ConvexQueryRequest request, in InstalledSweepLeaf leaf,
        ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexLeafEvidence evidence)
    {
        evidence = default;
        if ((request.Operation != ConvexQueryOperation.Sweep && request.Displacement != Vector3.Zero) || !ConvexQueryPolicy.Contains(request) ||
            scratch is null || leaf.Geometry is not { } geometry || !leaf.Map.IsResolved ||
            geometry.Curved.HasValue || geometry.Children is not null || geometry.Mesh is not null || geometry.IsTriangle ||
            !geometry.Matches(geometry.Owner, geometry.Root) ||
            !geometry.TryPose(geometry.ParentPose, out _) || geometry.LeafPose.Position != leaf.Pose.Position ||
            geometry.LeafPose.Orientation != leaf.Pose.Orientation ||
            !InstalledPoseOperator.TryCreateSupport(leaf.Pose, out var map) || !work.Primitive()) return false;
        // This operation uses only the represented forward map. There is no assumed rigid inverse
        // and no rounded proposal to reconcile. The installed scalar/SIMD coefficients must agree.
        var represented = new InstalledSweepLeaf(geometry, leaf.Pose, map, operational: true);
        try
        {
            for (int i = 0; i < geometry.VertexCount; i++)
            {
                if (!work.Feature()) return false;
                P p = ConvexPlanarFeatures.Vertex(represented, i, Vector3.Zero);
                foreach (Q c in new[] { p.X, p.Y, p.Z })
                    if ((Q.From(2048) - c).Sign < 0 || (Q.From(2048) + c).Sign < 0) return false;
            }
            return request.Operation == ConvexQueryOperation.Sweep
                ? ConvexPlanarSweep.TryEvaluate(request, represented, ref work, scratch, out evidence)
                : request.Operation is ConvexQueryOperation.Contacts or ConvexQueryOperation.Penetration
                ? ConvexPlanarContacts.TryEvaluate(request, represented, ref work, scratch, out evidence)
                : TryDistance(request, represented, ref work, scratch, out evidence);
        }
        catch (ConvexExactRefusalException)
        {
            evidence = default;
            return work.Refuse();
        }
    }

    internal static bool TryDistance(in ConvexQueryRequest request, in InstalledSweepLeaf leaf,
        ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexLeafEvidence evidence)
    {
        var context = new ConvexDistanceContext(request, ConvexPlanarSource.FromRepresented(leaf), ConvexCertificateFraction.From(0), scratch, true);
        bool accepted = TryDistance(context, ref work, out var result);
        evidence = result.Evidence;
        return accepted;
    }

    internal static bool TryDistanceCertificate(in ConvexQueryRequest request, in ConvexPlanarSource source,
        ConvexCertificateFraction fraction, ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexDistanceCertificate certificate,
        P translation = default, bool replace = true)
    {
        certificate = default;
        if (scratch is null || !source.IsValid || !fraction.IsValid || !ConvexQueryPolicy.Contains(request) || work.Exhausted || work.Descendants == 0) return false;
        if (fraction.Root is not null)
        {
            if (translation != default || !replace || fraction.Circular is null || !fraction.TryCompare(ConvexCertificateFraction.From(0), ref work, scratch, out int lower) || lower < 0 ||
                !fraction.TryCompare(ConvexCertificateFraction.From(1), ref work, scratch, out int upper) || upper > 0) return false;
            return fraction.Circular.TryAtFraction(request, source, fraction, ref work, scratch, out certificate);
        }
        if (replace) scratch.BeginCertificates();
        int checkpoint = scratch.Polynomials.Used;
        bool retained = false;
        try
        {
            for (int i = 0; i < source.VertexCount; i++)
            {
                if (!work.Feature()) return false;
                // The scope bound belongs to installed vertices. Temporal proposal points
                // also contain the translation, which the request and exact policy bound.
                P p = source.IsOriginal ? source.Vertex(i) :
                    ConvexPlanarFeatures.Vertex(source.Leaf, source.OriginalVertex(i), Vector3.Zero);
                foreach (Q c in new[] { p.X, p.Y, p.Z })
                    if ((Q.From(2048) - c).Sign < 0 || (Q.From(2048) + c).Sign < 0) return false;
            }
            var context = new ConvexDistanceContext(request, source, fraction, scratch, false, translation);
            if (!TryRetainedInterior(context, ref work, out var result) &&
                !TryDistance(context, ref work, out result)) return false;
            certificate = result.Certificate;
            scratch.RetainCertificates(checkpoint);
            retained = true;
            return true;
        }
        catch (ConvexExactRefusalException) { return work.Refuse(); }
        finally { if (!retained) scratch.Polynomials.Restore(checkpoint); }
    }

    // Retention can establish containment before enumerating distant boundary pairs. This
    // changes neither the legacy Distance acceptance order nor any encoded Distance value.
    static bool TryRetainedInterior(in ConvexDistanceContext context, ref ConvexQueryWork work, out ConvexStaticResult result)
    {
        result = default;
        var source = context.Source;
        if (source.Rank != 3 || source.FaceCount < 4 || !work.Cell()) return false;
        P p = new(default, (Q.From(context.Request.Shape.RingY) + Q.From(context.Request.Shape.UpperY)) / Q.From(2), default);
        for (int face = 0; face < source.FaceCount; face++)
        {
            if (!work.Feature()) return false;
            P a = source.Vertex(source.FaceVertex(face, 0), context.Feet);
            P n = P.Cross(source.Vertex(source.FaceVertex(face, 1), context.Feet) - a,
                source.Vertex(source.FaceVertex(face, source.InteriorFaceSize(face) - 1), context.Feet) - a);
            if (P.Dot(n, n).Sign <= 0 || P.Dot(n, p - a).Sign >= 0) return false;
        }
        return context.TryAccept(p, p, ConvexTargetFeature.Interior,
            source.FaceCount + source.EdgeCount + source.VertexCount, true, out result);
    }

    static bool TryDistance(in ConvexDistanceContext context, ref ConvexQueryWork work, out ConvexStaticResult result)
    {
        result = default;
        var g = context.Source;
        var request = context.Request;
        for (int face = 0; face < g.FaceCount; face++)
        {
            if (!work.Feature() || !work.Cell()) return false;
            P a = g.Vertex(g.FaceVertex(face, 0), context.Feet);
            P b = g.Vertex(g.FaceVertex(face, 1), context.Feet);
            P c = g.Vertex(g.FaceVertex(face, g.InteriorFaceSize(face) - 1), context.Feet);
            P n = P.Cross(c - a, b - a);
            Q norm = P.Dot(n, n);
            if (norm.Sign <= 0) return false;
            P q = Support(request.Shape, n), t = q + n * (P.Dot(a - q, n) / norm);
            if (ConvexPlanarFeatures.OnFace(g, face, context.Feet, t))
            {
                if (ConvexPlanarFeatures.Member(request.Shape, t) &&
                    context.TryAccept(t, t, ConvexTargetFeature.Face, face, true, out result)) return true;
                if (TryPair(context, q, t, ConvexTargetFeature.Face, face, ref work, out result)) return true;
            }
            // A plane section wholly inside a finite face has no edge crossing. The segment
            // between opposing attained supports lies in the shell and crosses every such plane.
            P opposite = Support(request.Shape, n * Q.From(-1)), across = q - opposite;
            Q span = P.Dot(across, n);
            if (span.Sign <= 0) return false;
            Q fraction = P.Dot(a - opposite, n) / span;
            if (fraction.Sign < 0 || (Q.From(1) - fraction).Sign < 0) continue;
            P section = opposite + across * fraction;
            if (ConvexPlanarFeatures.OnFace(g, face, context.Feet, section) &&
                ConvexPlanarFeatures.Member(request.Shape, section) &&
                context.TryAccept(section, section, ConvexTargetFeature.Face, face, true, out result)) return true;
        }
        for (int vertex = 0; vertex < g.VertexCount; vertex++)
        {
            P t = g.Vertex(vertex, context.Feet);
            if (TryPoint(context, t, ConvexTargetFeature.Vertex, g.FaceCount + g.EdgeCount + vertex,
                ref work, out result)) return true;
            if (work.Exhausted) return false;
        }
        for (int edge = 0; edge < g.EdgeCount; edge++)
        {
            var ends = g.Edge(edge);
            P a = g.Vertex(ends.A, context.Feet);
            P v = g.Vertex(ends.B, context.Feet) - a;
            if (ConvexPlanarEdges.TryEvaluate(context, a, v, g.FaceCount + edge, ref work, out result)) return true;
            if (work.Exhausted) return false;
            if (context.Scratch.CircularEdges.TryEvaluate(context, a, v, g.FaceCount + edge, ref work, out result)) return true;
            if (work.Exhausted) return false;
        }
        // Containment has an attained common point even when no target boundary meets the shell.
        if (g.Rank < 2) return false;
        foreach (float height in new[] { request.Shape.LowerY, request.Shape.RingY, request.Shape.UpperY })
        {
            if (!work.Feature() || !work.Cell()) return false;
            P p = new(default, Q.From(height), default);
            if (ConvexPlanarFeatures.InTarget(g, context.Feet, p) &&
                context.TryAccept(p, p, ConvexTargetFeature.Interior,
                    g.FaceCount + g.EdgeCount + g.VertexCount, true, out result)) return true;
        }
        result = default;
        return false;
    }

    internal static bool TryCommonSupportingNormal(in ConvexDistanceCertificate pair, P common, P towardTarget,
        ref ConvexQueryWork work)
    {
        if (!pair.IsAlive || pair.IsCircular || !pair.Source.IsOriginal || pair.Fraction.Root is not null ||
            !work.Feature() || !work.Cell() || P.Dot(towardTarget, towardTarget).Sign <= 0) return false;
        P feet = pair.Feet;
        if (!ConvexPlanarFeatures.Member(pair.Request.Shape, common) ||
            !ConvexPlanarFeatures.NormalCone(pair.Request.Shape, common, towardTarget)) return false;
        for (int face = 0; face < pair.Source.FaceCount; face++)
            if (!work.Feature()) return false;
        if (!ConvexPlanarFeatures.InTarget(pair.Source, feet, common)) return false;
        return ConvexPlanarFeatures.SupportsTarget(pair.Source, feet, common, towardTarget, ref work);
    }

    internal static P Support(in ConvexQueryShape shape, P n)
    {
        Q r = Q.From(shape.Radius), lower = Q.From(shape.LowerY), ring = Q.From(shape.RingY), upper = Q.From(shape.UpperY);
        Q radial = Q.Root((n.X * n.X + n.Z * n.Z).A);
        if (radial.Sign == 0) return new(default, n.Y.Sign > 0 ? upper : lower, default);
        Q shoulderAdvantage = (ring - lower) * n.Y + r * radial;
        if (shoulderAdvantage.Sign < 0) return new(default, lower, default);
        Q y = n.Y.Sign > 0 ? upper : n.Y.Sign == 0 ? (ring + upper) / Q.From(2) : ring;
        Q radius = r;
        if (shoulderAdvantage.Sign == 0) { y = (lower + ring) / Q.From(2); radius = r / Q.From(2); }
        return new(n.X * radius / radial, y, n.Z * radius / radial);
    }

    internal static bool TryPoint(in ConvexDistanceContext context, P t,
        ConvexTargetFeature targetFeature, int featureId, ref ConvexQueryWork work, out ConvexStaticResult result)
    {
        result = default;
        var request = context.Request;
        if (!work.Feature() || !work.Cell()) return false;
        if (ConvexPlanarFeatures.Member(request.Shape, t))
            return context.TryAccept(t, t, targetFeature, featureId, true, out result);
        Q r = Q.From(request.Shape.Radius), lower = Q.From(request.Shape.LowerY), ring = Q.From(request.Shape.RingY), upper = Q.From(request.Shape.UpperY);
        P apex = new(default, lower, default);
        if (TryPair(context, apex, t, targetFeature, featureId, ref work, out result)) return true;
        P head = new(t.X, upper, t.Z);
        if (TryPair(context, head, t, targetFeature, featureId, ref work, out result)) return true;
        Q square = t.X * t.X + t.Z * t.Z;
        if (!square.Rational || square.Sign <= 0) return false;
        Q radial = Q.Root(square.A);
        Q h = ring - lower, parameter = (r * radial + h * (t.Y - lower)) / (r * r + h * h);
        P cone = new(t.X * (r * parameter / radial), lower + h * parameter, t.Z * (r * parameter / radial));
        if (TryPair(context, cone, t, targetFeature, featureId, ref work, out result)) return true;
        P shoulder = new(t.X * (r / radial), ring, t.Z * (r / radial));
        if (TryPair(context, shoulder, t, targetFeature, featureId, ref work, out result)) return true;
        P side = new(shoulder.X, t.Y, shoulder.Z);
        if (TryPair(context, side, t, targetFeature, featureId, ref work, out result)) return true;
        P rim = new(shoulder.X, upper, shoulder.Z);
        return TryPair(context, rim, t, targetFeature, featureId, ref work, out result);
    }

    internal static bool TryPair(in ConvexDistanceContext context, P q, P t,
        ConvexTargetFeature targetFeature, int featureId, ref ConvexQueryWork work, out ConvexStaticResult result)
    {
        result = default;
        var request = context.Request;
        if (!work.Feature() || !work.Cell()) return false;
        if (!ConvexPlanarFeatures.Member(request.Shape, q)) return false;
        P towardTarget = t - q;
        if (!ConvexPlanarFeatures.NormalCone(request.Shape, q, towardTarget) ||
            !ConvexPlanarFeatures.SupportsTarget(context.Source, context.Feet, t, towardTarget, ref work)) return false;
        return context.TryAccept(q, t, targetFeature, featureId, false, out result);
    }
}
