using System;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Original endpoint attainment seeds the labelled source. Only exact affine
/// membership selects a sub-simplex. No extruded target topology is constructed.</summary>
internal static class ConvexTemporalSeed
{
    internal static bool TryCreate(in ConvexQueryRequest request, in ConvexPlanarSource original,
        double from, double to, in ConvexDistanceCertificate supplied, ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexTemporalSimplex simplex)
    {
        simplex = null!;
        ConvexDistanceCertificate endpoint;
        double at = to;
        if (supplied.IsAlive)
        {
            if (!supplied.IsOwnedBy(scratch) || !supplied.Source.IsOriginal || !supplied.Source.IsValid ||
                !supplied.Request.Equals(request) || supplied.Translation != default ||
                !ReferenceEquals(supplied.Source.Leaf.Geometry, original.Leaf.Geometry) ||
                !supplied.Source.Leaf.Pose.Equals(original.Leaf.Pose) || supplied.Fraction.Root is not null) return false;
            if (!supplied.Fraction.TryCompare(ConvexCertificateFraction.From(to), ref work, scratch, out int last)) return false;
            if (last != 0)
            {
                if (!supplied.Fraction.TryCompare(ConvexCertificateFraction.From(from), ref work, scratch, out int first) || first != 0) return false;
                at = from;
            }
            endpoint = supplied;
        }
        else if (!ConvexFeatureKernel.TryDistanceCertificate(request, original, ConvexCertificateFraction.From(to),
            ref work, scratch, out endpoint, replace: false)) return false;
        // Zero is the exact global minimum. Otherwise compare both attained endpoint bounds.
        if (!supplied.IsAlive && from != to && (endpoint.IsCircular || endpoint.BaseSquaredDistance.Sign > 0))
        {
            if (!ConvexFeatureKernel.TryDistanceCertificate(request, original, ConvexCertificateFraction.From(from),
                ref work, scratch, out var first, replace: false) ||
                !first.TryCompareDistance(endpoint, ref work, scratch, out int compare)) return false;
            if (compare < 0) { endpoint = first; at = from; }
        }
        int[] ids = FeatureVertices(endpoint);
        if (ids.Length == 0) return false;
        ConvexTemporalVertex[] chosen;
        if (endpoint.IsCircular)
        {
            // The retained stationary witness lies on this closed original edge.
            chosen = [ConvexTemporalVertex.Create(request, original, ids[0], at),
                ConvexTemporalVertex.Create(request, original, ids[1], at)];
        }
        else if (!TrySelect(request, original, ids, endpoint.Target, at, ref work, out chosen)) return false;
        if (!ConvexTemporalSimplex.TryCreate(request, original, chosen, out simplex)) return false;
        // Lift the attained feature to the other endpoint, retaining only affine-independent
        // slots. An attained seed remains inside every enlargement.
        double other = at == to ? from : to;
        if (other == at) return true;
        foreach (int id in ids)
        {
            if (chosen.Length == 4) break;
            if (!work.Feature()) return false;
            var enlarged = new ConvexTemporalVertex[chosen.Length + 1];
            chosen.CopyTo(enlarged, 0);
            enlarged[^1] = ConvexTemporalVertex.Create(request, original, id, other);
            if (!ConvexTemporalSimplex.TryCreate(request, original, enlarged, out var trial)) continue;
            chosen = enlarged; simplex = trial;
        }
        return true;
    }

    static int[] FeatureVertices(in ConvexDistanceCertificate pair)
    {
        var source = pair.Source;
        int id = pair.LocalFeatureId;
        if (pair.TargetFeature == ConvexTargetFeature.Vertex) return [id - source.FaceCount - source.EdgeCount];
        if (pair.TargetFeature == ConvexTargetFeature.Edge)
        { var e = source.Edge(id - source.FaceCount); return [e.A, e.B]; }
        int count = pair.TargetFeature == ConvexTargetFeature.Face ? source.InteriorFaceSize(id) : source.VertexCount;
        var ids = new int[count];
        for (int i = 0; i < count; i++) ids[i] = pair.TargetFeature == ConvexTargetFeature.Face ? source.FaceVertex(id, i) : i;
        return ids;
    }

    static bool TrySelect(in ConvexQueryRequest request, in ConvexPlanarSource original, int[] ids,
        P target, double at, ref ConvexQueryWork work, out ConvexTemporalVertex[] chosen)
    {
        // The accepted static source already uses these exact affine membership predicates.
        // Vertices, edges and a convex face's fan cover its finite closed feature.
        chosen = [];
        P point = target;
        for (int count = 1; count <= 4; count++)
            for (int a = 0; a < ids.Length; a++)
                for (int b = a + 1; b < (count > 1 ? ids.Length : a + 2); b++)
                    for (int c = b + 1; c < (count > 2 ? ids.Length : b + 2); c++)
                        for (int d = c + 1; d < (count > 3 ? ids.Length : c + 2); d++)
                        {
                            if (!work.Feature()) return false;
                            int[] indices = [a, b, c, d];
                            var vertices = new ConvexTemporalVertex[count];
                            for (int i = 0; i < count; i++) vertices[i] = ConvexTemporalVertex.Create(request, original, ids[indices[i]], at);
                            if (!ConvexTemporalSimplex.TryCreate(request, original, vertices, out var candidate)) continue;
                            if (!candidate.TryCoordinates(point, ref work, out var weights, out _))
                            { if (work.Exhausted) return false; continue; }
                            int active = 0;
                            for (int i = 0; i < count; i++) if (weights[i].Sign > 0) active++;
                            chosen = new ConvexTemporalVertex[active];
                            int slot = 0;
                            for (int i = 0; i < count; i++) if (weights[i].Sign > 0) chosen[slot++] = vertices[i];
                            return true;
                        }
        return false;
    }
}
