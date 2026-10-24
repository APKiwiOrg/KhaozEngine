using System.Numerics;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Exact source-backed planar feature access and closed convex predicates.</summary>
internal static class ConvexPlanarFeatures
{
    internal static P Vertex(in InstalledSweepLeaf leaf, int index, Vector3 feet)
    {
        InstalledPoseOperator map = leaf.Map;
        Vector3 v = leaf.Geometry.Vertex(index);
        P x = Exact(map.RepresentedX), y = Exact(map.RepresentedY), z = Exact(map.RepresentedZ);
        return x * Q.From(v.X) + y * Q.From(v.Y) + z * Q.From(v.Z) + P.From(leaf.Pose.Position) - P.From(feet);
    }

    static P Exact(FeaturePoint p)
    {
        if (!p.IsExact) throw new ConvexExactRefusalException();
        return new(Q.From(p.X.Value), Q.From(p.Y.Value), Q.From(p.Z.Value));
    }

    internal static bool Member(in ConvexQueryShape shape, P p)
    {
        Q r = Q.From(shape.Radius), lower = Q.From(shape.LowerY), ring = Q.From(shape.RingY), upper = Q.From(shape.UpperY);
        if ((p.Y - lower).Sign < 0 || (upper - p.Y).Sign < 0) return false;
        Q allowed = (p.Y - ring).Sign < 0 ? r * (p.Y - lower) / (ring - lower) : r;
        return (allowed * allowed - p.X * p.X - p.Z * p.Z).Sign >= 0;
    }

    // A support certificate bounds both complete continuous discs, never sampled radial points.
    // Nonnegative gap is retained before squaring, including the sharp apex and shoulder ties.
    internal static bool NormalCone(in ConvexQueryShape shape, P p, P outward)
    {
        Q attained = P.Dot(p, outward), r = Q.From(shape.Radius);
        if ((attained - Q.From(shape.LowerY) * outward.Y).Sign < 0) return false;
        foreach (float height in new[] { shape.RingY, shape.UpperY })
        {
            Q gap = attained - Q.From(height) * outward.Y;
            if (gap.Sign < 0 || (gap * gap - r * r * (outward.X * outward.X + outward.Z * outward.Z)).Sign < 0) return false;
        }
        return true;
    }

    internal static bool SupportsTarget(in InstalledSweepLeaf leaf, Vector3 feet, P t, P towardTarget, ref ConvexQueryWork work) =>
        SupportsTarget(ConvexPlanarSource.FromRepresented(leaf), P.From(feet), t, towardTarget, ref work);

    internal static bool OnFace(in InstalledSweepLeaf leaf, int face, Vector3 feet, P t) =>
        OnFace(ConvexPlanarSource.FromRepresented(leaf), face, P.From(feet), t);

    internal static bool SupportsTarget(in ConvexPlanarSource leaf, P feet, P t, P towardTarget,
        ref ConvexQueryWork work)
    {
        for (int i = 0; i < leaf.VertexCount; i++)
        {
            if (!work.Feature()) return false;
            if (P.Dot(leaf.Vertex(i, feet) - t, towardTarget).Sign < 0) return false;
        }
        return true;
    }

    internal static bool OnFace(in ConvexPlanarSource leaf, int face, P feet, P t)
    {
        var g = leaf;
        P a = leaf.Vertex(g.FaceVertex(face, 0), feet);
        P normal = P.Cross(leaf.Vertex(g.FaceVertex(face, 1), feet) - a,
            leaf.Vertex(g.FaceVertex(face, g.InteriorFaceSize(face) - 1), feet) - a);
        if (P.Dot(normal, t - a).Sign != 0) return false;
        for (int i = 0; i < g.InteriorFaceSize(face); i++)
        {
            P first = leaf.Vertex(g.FaceVertex(face, i), feet);
            P edge = leaf.Vertex(g.FaceVertex(face, (i + 1) % g.InteriorFaceSize(face)), feet) - first;
            if (P.Dot(P.Cross(edge, t - first), normal).Sign < 0) return false;
        }
        return true;
    }

    internal static bool InTarget(in ConvexPlanarSource leaf, P feet, P p)
    {
        if (leaf.Rank == 2) return OnFace(leaf, 0, feet, p);
        if (leaf.Rank < 2) return false;
        for (int face = 0; face < leaf.FaceCount; face++)
        {
            var g = leaf;
            P a = leaf.Vertex(g.FaceVertex(face, 0), feet);
            P n = P.Cross(leaf.Vertex(g.FaceVertex(face, 1), feet) - a,
                leaf.Vertex(g.FaceVertex(face, g.InteriorFaceSize(face) - 1), feet) - a);
            if (P.Dot(n, p - a).Sign > 0) return false;
        }
        return true;
    }

    internal static ConvexShellFeature Classify(in ConvexQueryShape shape, P q)
    {
        if ((q.Y - Q.From(shape.LowerY)).Sign == 0) return ConvexShellFeature.Apex;
        bool radial = (q.X * q.X + q.Z * q.Z - Q.From(shape.Radius) * Q.From(shape.Radius)).Sign == 0;
        if ((q.Y - Q.From(shape.UpperY)).Sign == 0) return radial ? ConvexShellFeature.Rim : ConvexShellFeature.HeadDisc;
        if (radial) return (q.Y - Q.From(shape.RingY)).Sign == 0 ? ConvexShellFeature.Shoulder : ConvexShellFeature.Cylinder;
        return ConvexShellFeature.LowerCone;
    }
}
