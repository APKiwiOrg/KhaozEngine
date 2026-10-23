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
        if (request.Operation is not (ConvexQueryOperation.Distance or ConvexQueryOperation.Sweep) ||
            (request.Operation == ConvexQueryOperation.Distance && request.Displacement != Vector3.Zero) || !ConvexQueryPolicy.Contains(request) ||
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
        evidence = default;
        var g = leaf.Geometry;
        for (int face = 0; face < g.FaceCount; face++)
        {
            if (!work.Feature() || !work.Cell()) return false;
            P a = ConvexPlanarFeatures.Vertex(leaf, g.FaceVertex(face, 0), request.Pose.Position);
            P b = ConvexPlanarFeatures.Vertex(leaf, g.FaceVertex(face, 1), request.Pose.Position);
            P c = ConvexPlanarFeatures.Vertex(leaf, g.FaceVertex(face, g.InteriorFaceSize(face) - 1), request.Pose.Position);
            P n = P.Cross(c - a, b - a);
            Q norm = P.Dot(n, n);
            if (norm.Sign <= 0) return false;
            P q = Support(request.Shape, n), t = q + n * (P.Dot(a - q, n) / norm);
            if (ConvexPlanarFeatures.OnFace(leaf, face, request.Pose.Position, t))
            {
                if (ConvexPlanarFeatures.Member(request.Shape, t) &&
                    ConvexDistanceEncoding.TryCreate(request, t, t, ConvexTargetFeature.Face, face, true, out evidence)) return true;
                if (TryPair(request, leaf, q, t, ConvexTargetFeature.Face, face, ref work, out evidence)) return true;
            }
            // A plane section wholly inside a finite face has no edge crossing. The segment
            // between opposing attained supports lies in the shell and crosses every such plane.
            P opposite = Support(request.Shape, n * Q.From(-1)), across = q - opposite;
            Q span = P.Dot(across, n);
            if (span.Sign <= 0) return false;
            Q fraction = P.Dot(a - opposite, n) / span;
            if (fraction.Sign < 0 || (Q.From(1) - fraction).Sign < 0) continue;
            P section = opposite + across * fraction;
            if (ConvexPlanarFeatures.OnFace(leaf, face, request.Pose.Position, section) &&
                ConvexPlanarFeatures.Member(request.Shape, section) &&
                ConvexDistanceEncoding.TryCreate(request, section, section, ConvexTargetFeature.Face, face, true, out evidence)) return true;
        }
        for (int vertex = 0; vertex < g.VertexCount; vertex++)
        {
            P t = ConvexPlanarFeatures.Vertex(leaf, vertex, request.Pose.Position);
            if (TryPoint(request, leaf, t, ConvexTargetFeature.Vertex, g.FaceCount + g.EdgeCount + vertex,
                ref work, out evidence)) return true;
            if (work.Exhausted) return false;
        }
        for (int edge = 0; edge < g.EdgeCount; edge++)
        {
            var ends = g.Edge(edge);
            P a = ConvexPlanarFeatures.Vertex(leaf, ends.A, request.Pose.Position);
            P v = ConvexPlanarFeatures.Vertex(leaf, ends.B, request.Pose.Position) - a;
            if (ConvexPlanarEdges.TryEvaluate(request, leaf, a, v, g.FaceCount + edge, ref work, out evidence)) return true;
            if (work.Exhausted) return false;
            if (scratch.CircularEdges.TryEvaluate(request, leaf, a, v, g.FaceCount + edge, ref work, out evidence)) return true;
            if (work.Exhausted) return false;
        }
        // Containment has an attained common point even when no target boundary meets the shell.
        foreach (float height in new[] { request.Shape.LowerY, request.Shape.RingY, request.Shape.UpperY })
        {
            if (!work.Feature() || !work.Cell()) return false;
            P p = new(default, Q.From(height), default);
            if (ConvexPlanarFeatures.InTarget(leaf, request.Pose.Position, p) &&
                ConvexDistanceEncoding.TryCreate(request, p, p, ConvexTargetFeature.Interior,
                    g.FaceCount + g.EdgeCount + g.VertexCount, true, out evidence)) return true;
        }
        evidence = default;
        return false;
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

    internal static bool TryPoint(in ConvexQueryRequest request, in InstalledSweepLeaf leaf, P t,
        ConvexTargetFeature targetFeature, int featureId, ref ConvexQueryWork work, out ConvexLeafEvidence evidence)
    {
        evidence = default;
        if (!work.Feature() || !work.Cell()) return false;
        if (ConvexPlanarFeatures.Member(request.Shape, t))
            return ConvexDistanceEncoding.TryCreate(request, t, t, targetFeature, featureId, true, out evidence);
        Q r = Q.From(request.Shape.Radius), lower = Q.From(request.Shape.LowerY), ring = Q.From(request.Shape.RingY), upper = Q.From(request.Shape.UpperY);
        P apex = new(default, lower, default);
        if (TryPair(request, leaf, apex, t, targetFeature, featureId, ref work, out evidence)) return true;
        P head = new(t.X, upper, t.Z);
        if (TryPair(request, leaf, head, t, targetFeature, featureId, ref work, out evidence)) return true;
        Q square = t.X * t.X + t.Z * t.Z;
        if (!square.Rational || square.Sign <= 0) return false;
        Q radial = Q.Root(square.A);
        Q h = ring - lower, parameter = (r * radial + h * (t.Y - lower)) / (r * r + h * h);
        P cone = new(t.X * (r * parameter / radial), lower + h * parameter, t.Z * (r * parameter / radial));
        if (TryPair(request, leaf, cone, t, targetFeature, featureId, ref work, out evidence)) return true;
        P shoulder = new(t.X * (r / radial), ring, t.Z * (r / radial));
        if (TryPair(request, leaf, shoulder, t, targetFeature, featureId, ref work, out evidence)) return true;
        P side = new(shoulder.X, t.Y, shoulder.Z);
        if (TryPair(request, leaf, side, t, targetFeature, featureId, ref work, out evidence)) return true;
        P rim = new(shoulder.X, upper, shoulder.Z);
        return TryPair(request, leaf, rim, t, targetFeature, featureId, ref work, out evidence);
    }

    internal static bool TryPair(in ConvexQueryRequest request, in InstalledSweepLeaf leaf, P q, P t,
        ConvexTargetFeature targetFeature, int featureId, ref ConvexQueryWork work, out ConvexLeafEvidence evidence)
    {
        evidence = default;
        if (!work.Feature() || !work.Cell()) return false;
        if (!ConvexPlanarFeatures.Member(request.Shape, q)) return false;
        P towardTarget = t - q;
        if (!ConvexPlanarFeatures.NormalCone(request.Shape, q, towardTarget) ||
            !ConvexPlanarFeatures.SupportsTarget(leaf, request.Pose.Position, t, towardTarget, ref work)) return false;
        return ConvexDistanceEncoding.TryCreate(request, q, t, targetFeature, featureId, false, out evidence);
    }
}
