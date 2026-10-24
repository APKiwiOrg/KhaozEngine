using System;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Stationary original feature pairs propose exits. Only a complete inner-CSO
/// inball proof and an independently checked encoded separator certify a member minimum.</summary>
internal static class ConvexPlanarPenetration
{
    readonly record struct Candidate(P Query, P Target, P Normal, ConvexTargetFeature Feature, int Id, double Upper);

    internal static bool TryEvaluate(in ConvexQueryRequest request, in InstalledSweepLeaf leaf,
        ConvexCertificateFraction fraction, ref ConvexQueryWork work, ConvexQueryScratch scratch,
        out ConvexPenetrationCertificate certificate)
    {
        certificate = default;
        if (!ConvexPlanarContacts.TryClassify(request, leaf, fraction, ref work, scratch, out var initial) ||
            initial.State != ConvexInitialState.Overlapping) return false;
        return TryEvaluate(initial, ref work, scratch, out certificate);
    }

    internal static bool TryEvaluate(in ConvexInitialEvidence initial, ref ConvexQueryWork work,
        ConvexQueryScratch scratch, out ConvexPenetrationCertificate certificate)
    {
        certificate = default; bool accepted = false;
        if (!initial.IsAlive || !initial.Pair.IsOwnedBy(scratch) || initial.State != ConvexInitialState.Overlapping ||
            work.Exhausted || initial.Pair.Fraction.Root is not null) return false;
        try
        {
            var pair = initial.Pair; var request = pair.Request; var source = pair.Source;
            P feet = P.From(request.Pose.Position) + P.From(request.Displacement) * pair.Fraction.Value;
            Candidate best = default;
            if (!TryFaces(request, source, feet, ref work, ref best)) return false;
            if (!TryVertices(request, source, feet, ref work, ref best)) return false;
            if (best.Normal == default || !scratch.Penetration.TrySeed(request, source, feet, ref work)) return false;
            while (!work.Exhausted)
            {
                if (!scratch.Penetration.TryLower(ref work, out var face, out var radius)) return false;
                if (!work.Cell() || !work.Feature()) return false;
                if (ConvexPenetrationCertificate.TryCreate(initial, best.Query, best.Target, best.Normal,
                    best.Feature, best.Id, radius, ref work, out var result))
                { certificate = result; accepted = true; return true; }
                if (!scratch.Penetration.TryExpand(request, source, feet, face, ref work)) return false;
            }
            return false;
        }
        catch (ConvexExactRefusalException) { return work.Refuse(); }
        finally { if (!accepted) { scratch.Penetration.Reset(); scratch.BeginCertificates(); } }
    }

    static bool TryFaces(in ConvexQueryRequest request, in ConvexPlanarSource source, P feet,
        ref ConvexQueryWork work, ref Candidate best)
    {
        for (int face = 0; face < source.FaceCount; face++)
        {
            if (!work.Cell() || !work.Feature()) return false;
            P a = source.Vertex(source.FaceVertex(face, 0), feet);
            P n = P.Cross(source.Vertex(source.FaceVertex(face, source.InteriorFaceSize(face) - 1), feet) - a,
                source.Vertex(source.FaceVertex(face, 1), feet) - a);
            Q squared = P.Dot(n, n); if (squared.Sign <= 0) return false;
            P q = ConvexFeatureKernel.Support(request.Shape, n), t = q - n * (P.Dot(q - a, n) / squared);
            if (ConvexPlanarFeatures.OnFace(source, face, feet, t) &&
                !TryCandidate(request, source, feet, q, t, n, ConvexTargetFeature.Face, face, ref work, ref best) && work.Exhausted) return false;
        }
        return true;
    }

    static bool TryVertices(in ConvexQueryRequest request, in ConvexPlanarSource source, P feet,
        ref ConvexQueryWork work, ref Candidate best)
    {
        for (int vertex = 0; vertex < source.VertexCount; vertex++)
        {
            if (!work.Feature() || !work.Cell()) return false;
            P t = source.Vertex(vertex, feet);
            Q square = t.X * t.X + t.Z * t.Z;
            Q r = Q.From(request.Shape.Radius), lower = Q.From(request.Shape.LowerY), ring = Q.From(request.Shape.RingY), upper = Q.From(request.Shape.UpperY);
            int id = source.FaceCount + source.EdgeCount + vertex;
            P head = new(t.X, upper, t.Z), apex = new(default, lower, default);
            TryCandidate(request, source, feet, head, t, head - t, ConvexTargetFeature.Vertex, id, ref work, ref best);
            TryCandidate(request, source, feet, apex, t, apex - t, ConvexTargetFeature.Vertex, id, ref work, ref best);
            if (square.Sign > 0 && square.Rational)
            {
                Q radial = Q.Root(square.A), h = ring - lower;
                Q s = (r * radial + h * (t.Y - lower)) / (r * r + h * h);
                foreach (P q in new P[] { new(t.X * (r * s / radial), lower + h * s, t.Z * (r * s / radial)),
                    new(t.X * (r / radial), ring, t.Z * (r / radial)), new(t.X * (r / radial), t.Y, t.Z * (r / radial)),
                    new(t.X * (r / radial), upper, t.Z * (r / radial)) })
                    TryCandidate(request, source, feet, q, t, q - t, ConvexTargetFeature.Vertex, id, ref work, ref best);
            }
            if (work.Exhausted) return false;
        }
        return true;
    }

    static bool TryCandidate(in ConvexQueryRequest request, in ConvexPlanarSource source, P feet,
        P q, P t, P n, ConvexTargetFeature feature, int id, ref ConvexQueryWork work, ref Candidate best)
    {
        if (!work.Feature() || !work.Cell()) return false;
        Q squared = P.Dot(n, n);
        if (squared.Sign <= 0 || !ConvexPlanarFeatures.Member(request.Shape, q) ||
            !ConvexPlanarFeatures.NormalCone(request.Shape, q, n) ||
            !ConvexPlanarFeatures.SupportsTarget(source, feet, t, n, ref work)) return false;
        P cross = P.Cross(q - t, n);
        if (P.Dot(cross, cross).Sign != 0) return false;
        var depth = P.Dot(q - t, n).Bounds.Divide(Q.RootBounds(squared)).Add(GeometryInterval.Exact(request.Shape.Margin));
        if (!depth.IsResolved || depth.Lower <= 0) return false;
        if (best.Normal == default || depth.Upper < best.Upper) best = new(q, t, n, feature, id, depth.Upper);
        return true;
    }
}
