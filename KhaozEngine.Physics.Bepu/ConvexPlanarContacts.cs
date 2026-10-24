using System;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Attained initial relations only. Neither a zero Distance nor failed normal search
/// proves overlap. Every accepted normal supports both complete original solids.</summary>
internal static class ConvexPlanarContacts
{
    internal static bool TryClassify(in ConvexQueryRequest request, in InstalledSweepLeaf leaf,
        ConvexCertificateFraction fraction, ref ConvexQueryWork work, ConvexQueryScratch scratch,
        out ConvexInitialEvidence evidence)
    {
        evidence = default;
        if (scratch is null || !ConvexPlanarSource.TryCreate(leaf, out var source)) return false;
        bool accepted = false;
        try
        {
            if (!ConvexFeatureKernel.TryDistanceCertificate(request, source, fraction, ref work, scratch, out var pair) ||
                !pair.TryCompareSquaredMargin(request.Shape.Margin, ref work, scratch, out int sign)) return false;
            if (sign > 0)
            {
                if (!ConvexDistanceEncoding.TryCreate(pair, out var distance)) return false;
                evidence = new(ConvexInitialState.Separated, pair, contact: distance);
            }
            else if (request.Shape.Margin > 0)
            {
                if (sign < 0) evidence = new(ConvexInitialState.Overlapping, pair, true,
                    pair.IsCircular ? default : pair.Target + Feet(pair));
                else
                {
                    if (!pair.TryCompareSquaredMargin(0, ref work, scratch, out int positive) || positive <= 0 ||
                        !TryRoundedTouch(pair, out evidence)) return false;
                }
            }
            else
            {
                if (sign != 0 || pair.IsCircular || fraction.Root is not null) return false;
                P common = pair.Query, feet = Feet(pair);
                if (TryNormal(pair, common, ref work, out var direction))
                {
                    if (!TryLocalFeature(source, feet, common, ref work, out var feature, out int id) ||
                        !TryEncodeTouch(pair, common + feet, direction, feature, id, out evidence)) return false;
                }
                else
                {
                    if (work.Exhausted || !TryStrictCommonPoint(pair, common, ref work, out var point)) return false;
                    evidence = new(ConvexInitialState.Overlapping, pair, true, point);
                }
            }
            accepted = true;
            return true;
        }
        catch (ConvexExactRefusalException) { return work.Refuse(); }
        finally
        {
            // A root-time caller loans its retained root. Ordinary replacement owns its storage.
            if (!accepted && fraction.Root is null) scratch.BeginCertificates();
        }
    }

    internal static bool TryTouchingConstraint(in ConvexInitialEvidence evidence, P outward,
        ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexLeafEvidence contact)
    {
        contact = default;
        if (!evidence.IsAlive || evidence.State != ConvexInitialState.Touching || work.Exhausted ||
            !evidence.Pair.IsOwnedBy(scratch) || evidence.Pair.IsCircular) return false;
        try
        {
            var pair = evidence.Pair;
            if (pair.Request.Shape.Margin == 0)
            {
                if (!ConvexFeatureKernel.TryCommonSupportingNormal(pair, pair.Query, outward, ref work)) return false;
            }
            else
            {
                if (!work.Feature() || P.Dot(outward, outward).Sign <= 0 ||
                    P.Dot(outward, pair.TowardTarget).Sign <= 0 ||
                    P.Dot(P.Cross(outward, pair.TowardTarget), P.Cross(outward, pair.TowardTarget)).Sign != 0) return false;
            }
            if (!TryEncodeTouch(pair, evidence.CommonPoint, outward, evidence.Contact.TargetFeature,
                evidence.Contact.LocalFeatureId, out var constraint)) return false;
            contact = constraint.Contact;
            return true;
        }
        catch (ConvexExactRefusalException) { return work.Refuse(); }
    }

    static P Feet(in ConvexDistanceCertificate pair) => P.From(pair.Request.Pose.Position) + P.From(pair.Request.Displacement) * pair.Fraction.Value;
    static GeometryVector Unit(GeometryVector direction, GeometryInterval length) =>
        new(direction.X.Divide(length), direction.Y.Divide(length), direction.Z.Divide(length));
    static GeometryVector Add(GeometryVector a, GeometryVector b) => new(a.X.Add(b.X), a.Y.Add(b.Y), a.Z.Add(b.Z));

    static bool TryRoundedTouch(in ConvexDistanceCertificate pair, out ConvexInitialEvidence evidence)
    {
        evidence = default;
        // Exact distance=m>0 was proved before encoding. Normalization is the mathematical
        // quotient by that positive length, including a retained circular stationary root.
        var normal = Unit(pair.NormalBounds, GeometryInterval.Exact(pair.Request.Shape.Margin));
        var d = P.From(pair.Request.Displacement).Bounds;
        var time = pair.Fraction.Bounds;
        var point = Add(pair.TargetBounds, new(d.X.Multiply(time), d.Y.Multiply(time), d.Z.Multiply(time)));
        if (!ConvexDistanceEncoding.TryEnclosed(pair.Request, point, point, normal, GeometryInterval.Exact(0),
            pair.QueryFeature, pair.TargetFeature, pair.LocalFeatureId, false, out var contact)) return false;
        evidence = new(ConvexInitialState.Touching, pair, true, pair.IsCircular ? default : pair.Target + Feet(pair),
            pair.IsCircular ? default : pair.TowardTarget, normal, contact);
        return true;
    }

    static bool TryEncodeTouch(in ConvexDistanceCertificate pair, P worldPoint, P direction,
        ConvexTargetFeature feature, int id, out ConvexInitialEvidence evidence)
    {
        evidence = default;
        Q squared = P.Dot(direction, direction);
        if (squared.Sign <= 0) return false;
        var normal = Unit(direction.Bounds, Q.RootBounds(squared));
        var point = (worldPoint - P.From(pair.Request.Pose.Position)).Bounds;
        if (!ConvexDistanceEncoding.TryEnclosed(pair.Request, point, point, normal, GeometryInterval.Exact(0),
            ConvexPlanarFeatures.Classify(pair.Request.Shape, pair.Query), feature, id, false, out var contact)) return false;
        evidence = new(ConvexInitialState.Touching, pair, true, worldPoint, direction, normal, contact);
        return true;
    }

    static P FaceNormal(in ConvexPlanarSource source, int face)
    {
        P a = source.Vertex(source.FaceVertex(face, 0));
        return P.Cross(source.Vertex(source.FaceVertex(face, 1)) - a,
            source.Vertex(source.FaceVertex(face, source.InteriorFaceSize(face) - 1)) - a);
    }

    static bool TryLocalFeature(in ConvexPlanarSource source, P feet, P common, ref ConvexQueryWork work,
        out ConvexTargetFeature feature, out int id)
    {
        feature = default; id = 0;
        for (int i = 0; i < source.VertexCount; i++)
        {
            if (!work.Feature()) return false;
            P delta = source.Vertex(i, feet) - common;
            if (P.Dot(delta, delta).Sign != 0) continue;
            feature = ConvexTargetFeature.Vertex; id = source.FaceCount + source.EdgeCount + i; return true;
        }
        for (int i = 0; i < source.EdgeCount; i++)
        {
            if (!work.Feature()) return false;
            var edge = source.Edge(i);
            P a = source.Vertex(edge.A, feet), v = source.Vertex(edge.B, feet) - a, delta = common - a;
            P cross = P.Cross(v, delta);
            Q projection = P.Dot(delta, v), length = P.Dot(v, v);
            if (P.Dot(cross, cross).Sign != 0 || projection.Sign < 0 || (length - projection).Sign < 0) continue;
            feature = ConvexTargetFeature.Edge; id = source.FaceCount + i; return true;
        }
        for (int i = 0; i < source.FaceCount; i++)
        {
            if (!work.Feature()) return false;
            if (!ConvexPlanarFeatures.OnFace(source, i, feet, common)) continue;
            feature = ConvexTargetFeature.Face; id = i; return true;
        }
        return false;
    }

    // At a smooth point there is one gradient. At either circular seam both incident
    // gradients remain closed. The apex has a circular cone, handled by the disc problem.
    static int Gradients(in ConvexQueryShape shape, P common, out P a, out P b)
    {
        a = b = default;
        Q r = Q.From(shape.Radius), h = Q.From(shape.RingY) - Q.From(shape.LowerY);
        Q y = common.Y - Q.From(shape.LowerY), radial = common.X * common.X + common.Z * common.Z;
        if (y.Sign == 0) return -1;
        bool cone = (common.Y - Q.From(shape.RingY)).Sign <= 0 && (h * h * radial - r * r * y * y).Sign == 0;
        bool side = (common.Y - Q.From(shape.RingY)).Sign >= 0 && (radial - r * r).Sign == 0;
        bool head = (common.Y - Q.From(shape.UpperY)).Sign == 0;
        int count = 0;
        if (cone) { a = new(h * h * common.X, -r * r * y, h * h * common.Z); count++; }
        if (side) { if (count == 0) a = new(common.X, default, common.Z); else b = new(common.X, default, common.Z); count++; }
        if (head) { if (count == 0) a = new(default, Q.From(1), default); else b = new(default, Q.From(1), default); count++; }
        return count;
    }

    static bool TryNormal(in ConvexDistanceCertificate pair, P common, ref ConvexQueryWork work, out P normal)
    {
        normal = default;
        var source = pair.Source;
        P feet = Feet(pair);
        for (int face = 0; face < source.FaceCount; face++)
        {
            if (!work.Feature() || !work.Cell()) return false;
            if (P.Dot(FaceNormal(source, face), common - source.Vertex(source.FaceVertex(face, 0), feet)).Sign != 0) continue;
            P n = FaceNormal(source, face) * Q.From(-1);
            if (ConvexFeatureKernel.TryCommonSupportingNormal(pair, common, n, ref work)) { normal = n; return true; }
        }
        int count = Gradients(pair.Request.Shape, common, out P a, out P b);
        if (count == -1)
        {
            if (!TryDisc(pair, common, false, ref work, out var n) ||
                !ConvexFeatureKernel.TryCommonSupportingNormal(pair, common, n, ref work)) return false;
            normal = n; return true;
        }
        if (count == 0) return false;
        if (ConvexFeatureKernel.TryCommonSupportingNormal(pair, common, a, ref work)) { normal = a; return true; }
        if (count == 1) return false;
        // Intersect the whole two-ray cone with all target inequalities. Select an attained
        // endpoint of this exact interval, never an average of independently accepted normals.
        Q lo = default, hi = Q.From(1);
        for (int i = 0; i < source.VertexCount; i++)
        {
            if (!work.Feature()) return false;
            P offset = source.Vertex(i, feet) - common;
            if (!Clip(P.Dot(offset, a), P.Dot(offset, b), false, ref lo, ref hi)) return false;
        }
        P direction = a + (b - a) * lo;
        if (!ConvexFeatureKernel.TryCommonSupportingNormal(pair, common, direction, ref work)) return false;
        normal = direction; return true;
    }

    // Intersect f(t)>=0 (or f(t)<0) on [lo,hi]. Strict endpoints are consumed only
    // through an interior midpoint, so no epsilon is introduced into an open predicate.
    static bool Clip(Q from, Q to, bool negative, ref Q lo, ref Q hi)
    {
        if (negative) { from = -from; to = -to; }
        Q slope = to - from;
        if (slope.Sign == 0) return negative ? from.Sign > 0 : from.Sign >= 0;
        Q root = -from / slope;
        if (slope.Sign > 0) { if ((root - lo).Sign > 0) lo = root; }
        else if ((hi - root).Sign > 0) hi = root;
        return negative ? (hi - lo).Sign > 0 : (hi - lo).Sign >= 0;
    }

    static bool StrictShell(in ConvexQueryShape shape, P p)
    {
        if ((p.Y - Q.From(shape.LowerY)).Sign <= 0 || (Q.From(shape.UpperY) - p.Y).Sign <= 0) return false;
        Q radius = Q.From(shape.Radius);
        if ((p.Y - Q.From(shape.RingY)).Sign < 0) radius *= (p.Y - Q.From(shape.LowerY)) / (Q.From(shape.RingY) - Q.From(shape.LowerY));
        return (radius * radius - p.X * p.X - p.Z * p.Z).Sign > 0;
    }

    static bool TryStrictPoint(in ConvexDistanceCertificate pair, P local, ref ConvexQueryWork work, out P world)
    {
        world = default;
        if (!work.Cell() || !work.Feature()) return false;
        P candidate = local + Feet(pair);
        // An algebraic proposal never becomes a rational proof by assertion. Verify the
        // rational encoding afresh against every strict inequality of both original solids.
        Q Rational(Q q) => q.Rational ? q : Q.From(q.Bounds.Lower * 0.5 + q.Bounds.Upper * 0.5);
        candidate = new(Rational(candidate.X), Rational(candidate.Y), Rational(candidate.Z));
        if (!StrictShell(pair.Request.Shape, candidate - Feet(pair))) return false;
        var source = pair.Source;
        if (!source.IsOriginal || source.Rank != 3 || source.FaceCount < 4) return false;
        for (int face = 0; face < source.FaceCount; face++)
        {
            if (!work.Feature()) return false;
            P n = FaceNormal(source, face), a = source.Vertex(source.FaceVertex(face, 0));
            if (P.Dot(n, n).Sign <= 0 || P.Dot(n, candidate - a).Sign >= 0) return false;
        }
        world = candidate; return true;
    }

    static bool Inward(in ConvexQueryShape shape, int count, P a, P b, P direction)
    {
        if (count >= 0) return (count == 0 || P.Dot(a, direction).Sign < 0) && (count < 2 || P.Dot(b, direction).Sign < 0);
        Q h = Q.From(shape.RingY) - Q.From(shape.LowerY), r = Q.From(shape.Radius);
        return direction.Y.Sign > 0 && (r * r * direction.Y * direction.Y - h * h * (direction.X * direction.X + direction.Z * direction.Z)).Sign > 0;
    }

    static bool TryStrictCommonPoint(in ConvexDistanceCertificate pair, P common, ref ConvexQueryWork work, out P world)
    {
        world = default;
        var source = pair.Source;
        P feet = Feet(pair), centroid = default;
        for (int i = 0; i < source.VertexCount; i++) { if (!work.Feature()) return false; centroid += source.Vertex(i, feet); }
        centroid *= Q.From(1) / Q.From(source.VertexCount);
        P axis = new(default, (Q.From(pair.Request.Shape.RingY) + Q.From(pair.Request.Shape.UpperY)) / Q.From(2), default);
        if (TryStrictPoint(pair, common, ref work, out world) || TryStrictPoint(pair, centroid, ref work, out world) ||
            TryStrictPoint(pair, axis, ref work, out world)) return true;
        int count = Gradients(pair.Request.Shape, common, out P a, out P b);
        P direction = centroid - common;
        bool found = Inward(pair.Request.Shape, count, a, b, direction);
        if (!found && count == -1) found = TryDisc(pair, common, true, ref work, out direction);
        for (int i = 0; !found && count >= 0 && i < source.VertexCount; i++)
        {
            if (!work.Feature()) return false;
            direction = source.Vertex(i, feet) - common;
            found = Inward(pair.Request.Shape, count, a, b, direction);
        }
        // Two active gradients need at most an edge of their two-dimensional image.
        for (int i = 0; !found && count == 2 && i < source.VertexCount; i++)
            for (int j = i + 1; !found && j < source.VertexCount; j++)
            {
                if (!work.Feature() || !work.Cell()) return false;
                P u = source.Vertex(i, feet) - common, v = source.Vertex(j, feet) - common;
                Q lo = default, hi = Q.From(1);
                if (!Clip(P.Dot(a, u), P.Dot(a, v), true, ref lo, ref hi) ||
                    !Clip(P.Dot(b, u), P.Dot(b, v), true, ref lo, ref hi)) continue;
                direction = u + (v - u) * ((lo + hi) / Q.From(2));
                found = Inward(pair.Request.Shape, count, a, b, direction);
            }
        if (!found || work.Exhausted) return false;
        Q blend = Q.From(1);
        while (!Inward(pair.Request.Shape, count, a, b, direction + (centroid - common) * blend))
        { if (!work.Cell()) return false; blend /= Q.From(2); }
        direction += (centroid - common) * blend;
        Q step = Q.From(1) / (Q.From(2) * (Q.From(1) + blend));
        while (!TryStrictPoint(pair, common + direction * step, ref work, out world))
        { if (work.Exhausted) return false; step /= Q.From(2); }
        return true;
    }

    // Apex feasibility is a disc cut by halfplanes. The minimum-norm feasible point is
    // the origin, a line projection or a line intersection. Each proposal is checked against
    // every constraint. Strict disc interior supplies an inward overlap direction. The closed
    // dual disc supplies a touching normal with Y=-1, so it can never be a zero vector.
    static bool TryDisc(in ConvexDistanceCertificate pair, P common, bool inward,
        ref ConvexQueryWork work, out P direction)
    {
        direction = default;
        var source = pair.Source;
        int count = inward ? source.FaceCount : source.VertexCount;
        Q y = Q.From(inward ? 1 : -1), ratio = (Q.From(pair.Request.Shape.RingY) - Q.From(pair.Request.Shape.LowerY)) / Q.From(pair.Request.Shape.Radius);
        if (inward) ratio = Q.From(1) / ratio;
        if (DiscFeasible(pair, common, inward, new(default, y, default), ratio, ref work)) { direction = new(default, y, default); return true; }
        for (int i = 0; i < count; i++)
        {
            if (!work.Feature() || !work.Cell()) return false;
            P a = Constraint(pair, common, inward, i);
            Q norm = a.X * a.X + a.Z * a.Z, rhs = -a.Y * y;
            if (norm.Sign == 0) continue;
            P p = new(a.X * rhs / norm, y, a.Z * rhs / norm);
            if (DiscFeasible(pair, common, inward, p, ratio, ref work)) { direction = p; return true; }
            for (int j = i + 1; j < count; j++)
            {
                if (!work.Feature() || !work.Cell()) return false;
                P b = Constraint(pair, common, inward, j);
                Q determinant = a.X * b.Z - a.Z * b.X, other = -b.Y * y;
                if (determinant.Sign == 0) continue;
                p = new((rhs * b.Z - a.Z * other) / determinant, y, (a.X * other - rhs * b.X) / determinant);
                if (DiscFeasible(pair, common, inward, p, ratio, ref work)) { direction = p; return true; }
            }
        }
        return false;
    }

    static P Constraint(in ConvexDistanceCertificate pair, P common, bool inward, int index)
    {
        if (!inward) return pair.Source.Vertex(index, Feet(pair)) - common;
        P n = FaceNormal(pair.Source, index);
        return P.Dot(n, common - pair.Source.Vertex(pair.Source.FaceVertex(index, 0), Feet(pair))).Sign == 0 ? n * Q.From(-1) : default;
    }

    static bool DiscFeasible(in ConvexDistanceCertificate pair, P common, bool inward, P p, Q ratio, ref ConvexQueryWork work)
    {
        if (!work.Feature()) return false;
        int sign = (ratio * ratio - p.X * p.X - p.Z * p.Z).Sign;
        if (inward ? sign <= 0 : sign < 0) return false;
        int count = inward ? pair.Source.FaceCount : pair.Source.VertexCount;
        for (int i = 0; i < count; i++)
        { if (!work.Feature() || P.Dot(Constraint(pair, common, inward, i), p).Sign < 0) return false; }
        return true;
    }
}
