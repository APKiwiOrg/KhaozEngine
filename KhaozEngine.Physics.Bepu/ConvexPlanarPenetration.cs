using System;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Faces, vertices and EPA facets propose directions to one static plane verifier.
/// The inner-CSO inball supplies the global lower proof. The verified plane supplies
/// its attained support witnesses, normal-cone direction and original local provenance.</summary>
internal static class ConvexPlanarPenetration
{
    internal static bool TryEvaluate(in ConvexQueryRequest request, in InstalledSweepLeaf leaf,
        ConvexCertificateFraction fraction, ref ConvexQueryWork work, ConvexQueryScratch scratch,
        out ConvexPenetrationCertificate certificate)
    {
        return TryEvaluate(request, leaf, fraction, ref work, scratch, out certificate, out _) && certificate.IsAlive;
    }

    internal static bool TryEvaluate(in ConvexQueryRequest request, in InstalledSweepLeaf leaf,
        ConvexCertificateFraction fraction, ref ConvexQueryWork work, ConvexQueryScratch scratch,
        out ConvexPenetrationCertificate certificate, out ConvexLeafEvidence contact)
    {
        certificate = default; contact = default;
        if (!fraction.IsValid || fraction.Root is not null || !fraction.Value.Rational ||
            scratch is null || !ConvexQueryPolicy.Contains(request) || work.Exhausted || work.Descendants == 0 ||
            !ConvexPlanarSource.TryCreate(leaf, out var source)) return false;
        bool accepted = false;
        scratch.BeginCertificates();
        try
        {
            for (int i = 0; i < source.VertexCount; i++)
            {
                if (!work.Feature()) return false;
                P p = source.Vertex(i);
                foreach (Q c in new[] { p.X, p.Y, p.Z })
                    if ((Q.From(2048) - c).Sign < 0 || (Q.From(2048) + c).Sign < 0) return false;
            }
            P feet = P.From(request.Pose.Position) + P.From(request.Displacement) * fraction.Value;
            ConvexPenetrationOrigin initial = default;
            ConvexPenetrationPlane best = default;
            if (scratch.Penetration.TrySeed(request, source, feet, ref work))
            {
                while (!work.Exhausted)
                {
                    if (!scratch.Penetration.TryLower(ref work, out var face, out var radius) ||
                        !ConvexPenetrationProposal.TryCreate(request, source, feet, face.Normal, ref work, out var proposal)) return false;
                    if (radius.IsResolved && radius.Lower > 0)
                    {
                        if (!initial.IsAlive)
                        {
                            // Every facet offset is strictly positive. The exact inner hull
                            // proves overlap before any full static feature classification.
                            initial = new(request, source, fraction, scratch, radius);
                            if (!TryFaces(request, source, feet, ref work, ref best) ||
                                !TryVertices(request, source, feet, ref work, ref best)) return false;
                        }
                        if (!TryCandidate(request, source, feet, proposal, ref work, ref best) && work.Exhausted) return false;
                        if (ConvexPenetrationCertificate.TryCreate(initial, best, radius, ref work, scratch, out var result))
                        { certificate = result; accepted = true; return true; }
                    }
                    if (proposal.Support.Upper <= 0 || !scratch.Penetration.TryExpand(request, face, proposal, ref work)) break;
                }
            }
            if (work.Exhausted || initial.IsAlive) return false;
            // An unproven hull cannot authorize overlap or a miss. The accepted classifier
            // supplies touching/separated evidence, or the requested operation refuses.
            if (!ConvexPlanarContacts.TryClassify(request, source, fraction, ref work, scratch, out var fallback) ||
                fallback.State == ConvexInitialState.Overlapping) return false;
            contact = fallback.Contact with { CertifiedErrorMetres = 0.001f };
            accepted = true;
            return true;
        }
        catch (ConvexExactRefusalException) { return work.Refuse(); }
        finally { if (!accepted) { scratch.Penetration.Reset(); scratch.BeginCertificates(); } }
    }

    static bool TryFaces(in ConvexQueryRequest request, in ConvexPlanarSource source, P feet,
        ref ConvexQueryWork work, ref ConvexPenetrationPlane best)
    {
        for (int face = 0; face < source.FaceCount; face++)
        {
            if (!work.Cell() || !work.Feature()) return false;
            P a = source.Vertex(source.FaceVertex(face, 0), feet);
            P n = P.Cross(source.Vertex(source.FaceVertex(face, source.InteriorFaceSize(face) - 1), feet) - a,
                source.Vertex(source.FaceVertex(face, 1), feet) - a);
            if (!TryCandidate(request, source, feet, n, ref work, ref best) && work.Exhausted) return false;
        }
        return true;
    }

    static bool TryVertices(in ConvexQueryRequest request, in ConvexPlanarSource source, P feet,
        ref ConvexQueryWork work, ref ConvexPenetrationPlane best)
    {
        for (int vertex = 0; vertex < source.VertexCount; vertex++)
        {
            if (!work.Feature() || !work.Cell()) return false;
            P t = source.Vertex(vertex, feet);
            Q square = t.X * t.X + t.Z * t.Z;
            Q r = Q.From(request.Shape.Radius), lower = Q.From(request.Shape.LowerY), ring = Q.From(request.Shape.RingY), upper = Q.From(request.Shape.UpperY);
            P head = new(t.X, upper, t.Z), apex = new(default, lower, default);
            TryCandidate(request, source, feet, head - t, ref work, ref best);
            TryCandidate(request, source, feet, apex - t, ref work, ref best);
            if (square.Sign > 0 && square.Rational)
            {
                Q radial = Q.Root(square.A), h = ring - lower;
                Q s = (r * radial + h * (t.Y - lower)) / (r * r + h * h);
                foreach (P q in new P[] { new(t.X * (r * s / radial), lower + h * s, t.Z * (r * s / radial)),
                    new(t.X * (r / radial), ring, t.Z * (r / radial)), new(t.X * (r / radial), t.Y, t.Z * (r / radial)),
                    new(t.X * (r / radial), upper, t.Z * (r / radial)) })
                    TryCandidate(request, source, feet, q - t, ref work, ref best);
            }
            if (work.Exhausted) return false;
        }
        return true;
    }

    static bool TryCandidate(in ConvexQueryRequest request, in ConvexPlanarSource source, P feet,
        P n, ref ConvexQueryWork work, ref ConvexPenetrationPlane best)
    {
        if (!ConvexPenetrationProposal.TryCreate(request, source, feet, n, ref work, out var proposal)) return false;
        return TryCandidate(request, source, feet, proposal, ref work, ref best);
    }

    static bool TryCandidate(in ConvexQueryRequest request, in ConvexPlanarSource source, P feet,
        ConvexPenetrationProposal proposal, ref ConvexQueryWork work, ref ConvexPenetrationPlane best)
    {
        // Any clearing vector for this direction is at least Support/|n| long.
        // Prune only with the certified lower endpoint, never a rounded estimate.
        if (best.Direction != default && proposal.Depth.Lower >= best.Length.Upper) return true;
        if (!ConvexPenetrationPlane.TryVerify(request, source, feet, proposal, ref work, out var plane)) return false;
        if (best.Direction == default || plane.Length.Upper < best.Length.Upper) best = plane;
        return true;
    }
}
