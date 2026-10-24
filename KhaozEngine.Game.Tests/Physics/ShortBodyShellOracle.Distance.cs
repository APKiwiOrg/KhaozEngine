using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Physics;

internal static partial class ShortBodyShellOracle
{
    // The caller supplies the evaluated shape and pose. No production witness, cached pose,
    // feature enumeration or CSO hull participates in this independent enclosure.
    internal static bool MemberTranslationEnclosedBy(in ConvexQueryShape shape, Pose pose,
        ConvexOracleTarget target, in ConvexPenetrationResult result)
    {
        if (!result.Complete || !result.Overlapping || pose.Orientation != Quaternion.Identity ||
            target.Vertices.Length < 4 || target.Faces.Length < 4) return false;
        var points = new R[target.Vertices.Length][];
        for (int i = 0; i < points.Length; i++)
        {
            var v = target.Vertices[i];
            points[i] = [new R(v.X.Numerator, v.X.Denominator) - R.From(pose.Position.X),
                new R(v.Y.Numerator, v.Y.Denominator) - R.From(pose.Position.Y),
                new R(v.Z.Numerator, v.Z.Denominator) - R.From(pose.Position.Z)];
        }
        R[] movement = [R.From(result.Translation.X), R.From(result.Translation.Y), R.From(result.Translation.Z)];
        R[] normal = [-movement[0], -movement[1], -movement[2]];
        R lengthSquared = DotR(movement, movement);
        if (lengthSquared.N.Sign <= 0 || lengthSquared.CompareTo(R.From(result.UpperDepth) * R.From(result.UpperDepth)) > 0) return false;
        I support = MemberSupport(shape, points, normal);
        if ((support.Upper + DotR(normal, movement)).N.Sign > 0) return false;
        // For an exterior base placement, min unit support is minus its distance. Growing
        // by the admitted ball adds exactly Margin to every unit support value.
        var distance = Entry(shape.WithMargin(0), pose, target, Vector3.Zero);
        if (distance.IsComplete && distance.Lower > 0)
            return (R.From(shape.Margin) - R.From(distance.Upper)).CompareTo(R.From(result.LowerDepth)) >= 0 &&
                (R.From(shape.Margin) - R.From(distance.Lower)).CompareTo(R.From(result.UpperDepth)) <= 0;
        return MemberInnerDepthAtLeast(shape, points, target.Faces, R.From(result.LowerDepth));
    }

    static R DotR(R[] a, R[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    static R[] SubR(R[] a, R[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
    static R[] CrossR(R[] a, R[] b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];

    static I MemberSupport(ConvexQueryShape shape, R[][] points, R[] n)
    {
        R least = DotR(points[0], n);
        foreach (var p in points) least = R.Min(least, DotR(p, n));
        I radial = Root(n[0] * n[0] + n[2] * n[2]) * R.From(shape.Radius);
        I ring = I.Point(R.From(shape.RingY) * n[1]) + radial;
        I head = I.Point(R.From(shape.UpperY) * n[1]) + radial;
        R apex = R.From(shape.LowerY) * n[1];
        return new I(R.Max(apex, R.Max(ring.Lower, head.Lower)) - least,
            R.Max(apex, R.Max(ring.Upper, head.Upper)) - least) + Root(DotR(n, n)) * R.From(shape.Margin);
    }

    // An independently authored rational polygon is an inner shell, not an oracle proxy
    // miss. The normal fan of a polytope sum comprises either summand's faces and all
    // edge cross products. Checking that complete fan proves a ball inside the inner sum.
    static bool MemberInnerDepthAtLeast(ConvexQueryShape shape, R[][] target, int[][] faces, R depth)
    {
        var ring = new List<R[]>();
        R radius = R.From(shape.Radius);
        for (int quadrant = 0; quadrant < 4; quadrant++)
            for (int i = 0; i < 16; i++)
            {
                R t = new(i, 16), denominator = R.One + t * t;
                R x = (R.One - t * t) / denominator, z = new R(2, 1) * t / denominator;
                for (int turn = 0; turn < quadrant; turn++) (x, z) = (-z, x);
                ring.Add([x * radius, R.From(shape.RingY), z * radius]);
            }
        R[] apex = [R.Zero, R.From(shape.LowerY), R.Zero];
        var shell = new List<R[]> { apex };
        var edges = new List<R[]>();
        var normals = new List<R[]> { new[] { R.Zero, R.One, R.Zero } };
        for (int i = 0; i < ring.Count; i++)
        {
            R[] a = ring[i], b = ring[(i + 1) % ring.Count];
            R[] edge = SubR(b, a), cone = SubR(a, apex);
            shell.Add(a); shell.Add([a[0], R.From(shape.UpperY), a[2]]);
            edges.Add(edge); edges.Add(cone);
            normals.Add([edge[2], R.Zero, -edge[0]]);
            normals.Add(CrossR(edge, SubR(apex, a)));
        }
        edges.Add([R.Zero, R.One, R.Zero]);
        // Topology is copied directly from the installed snapshot, independently of the
        // production source. Duplicate candidate normals are harmless exact predicates.
        var targetEdges = new HashSet<(int, int)>();
        // A normal of either orientation is checked, so hull winding is immaterial here.
        foreach (var face in faces)
        {
            normals.Add(CrossR(SubR(target[face[1]], target[face[0]]), SubR(target[face[^1]], target[face[0]])));
            for (int i = 0; i < face.Length; i++)
            {
                int a = face[i], b = face[(i + 1) % face.Length];
                targetEdges.Add(a < b ? (a, b) : (b, a));
            }
        }
        foreach (var (a, b) in targetEdges)
            foreach (var edge in edges) normals.Add(CrossR(edge, SubR(target[b], target[a])));
        var seen = new HashSet<(R, R, R)>();
        R required = R.Max(R.Zero, depth - R.From(shape.Margin));
        foreach (var n in normals)
        {
            R first = n[0].N.Sign != 0 ? n[0] : n[1].N.Sign != 0 ? n[1] : n[2];
            if (first.N.IsZero) continue;
            if (first.N.Sign < 0) first = -first;
            R[] canonical = [n[0] / first, n[1] / first, n[2] / first];
            if (!seen.Add((canonical[0], canonical[1], canonical[2]))) continue;
            if (!Check(canonical) || !Check([-canonical[0], -canonical[1], -canonical[2]])) return false;
        }
        return true;

        bool Check(R[] n)
        {
            R highest = DotR(shell[0], n), least = DotR(target[0], n);
            foreach (var q in shell) highest = R.Max(highest, DotR(q, n));
            foreach (var t in target) least = R.Min(least, DotR(t, n));
            R h = highest - least;
            return h.N.Sign >= 0 && (h * h - required * required * DotR(n, n)).N.Sign >= 0;
        }
    }

    // Meridian reduction is independent of the production feature/KKT construction. A full
    // Cartesian box has independent height and radial intervals. Distance decreases with radial
    // approach to the axis. Below the shoulder it decreases with height toward that shoulder.
    // Other installed convex snapshots use independently certified support and attained bounds.
    internal static ConvexOracleEvent Entry(in ConvexQueryShape shape, Pose pose, ConvexOracleTarget target, Vector3 displacement)
    {
        Validate(shape.Radius, shape.LowerY, shape.RingY, shape.UpperY, shape.Margin);
        if (pose.Orientation != Quaternion.Identity ||
            target.Vertices.Length < 4 || target.Faces.Length < 4) return default;
        if (displacement != Vector3.Zero) return SweepEntry(shape, pose, target, displacement);
        if (target.Vertices.Length != 8 || target.Faces.Length != 6) return GeneralEntry(shape, pose, target);
        R Copy(InstalledPoseOracle.R r) => new(r.Numerator, r.Denominator);
        R[] low = new R[3], high = new R[3];
        var points = new R[8][];
        for (int i = 0; i < 8; i++)
        {
            var v = target.Vertices[i];
            points[i] = [Copy(v.X) - R.From(pose.Position.X), Copy(v.Y) - R.From(pose.Position.Y), Copy(v.Z) - R.From(pose.Position.Z)];
            for (int j = 0; j < 3; j++)
            {
                low[j] = i == 0 ? points[i][j] : R.Min(low[j], points[i][j]);
                high[j] = i == 0 ? points[i][j] : R.Max(high[j], points[i][j]);
            }
        }
        int corners = 0;
        foreach (var p in points)
        {
            int mask = 0;
            for (int j = 0; j < 3; j++)
                if (p[j].CompareTo(high[j]) == 0) mask |= 1 << j;
                else if (p[j].CompareTo(low[j]) != 0) return GeneralEntry(shape, pose, target);
            if ((corners & (1 << mask)) != 0) return GeneralEntry(shape, pose, target);
            corners |= 1 << mask;
        }
        if (corners != 255) return GeneralEntry(shape, pose, target);
        R Near(R a, R b) => a.N.Sign > 0 ? a : b.N.Sign < 0 ? b : R.Zero;
        R x = Near(low[0], high[0]), z = Near(low[2], high[2]);
        R y = R.Max(low[1], R.Min(high[1], R.From(shape.RingY)));
        I best = MeridianDistance(shape, x, y, z);
        R margin = R.From(shape.Margin);
        var bounds = Outward(new(R.Max(R.Zero, best.Lower - margin), R.Max(R.Zero, best.Upper - margin)));
        return new(true, bounds.Lower, bounds.Upper);
    }

    static I MeridianDistance(in ConvexQueryShape shape, R x, R y, R z)
    {
        R radialSquare = x * x + z * z;
        R radius = R.From(shape.Radius), lower = R.From(shape.LowerY), ring = R.From(shape.RingY), upper = R.From(shape.UpperY);
        R allowed = y.CompareTo(ring) < 0 ? radius * (y - lower) / (ring - lower) : radius;
        if (y.CompareTo(lower) >= 0 && y.CompareTo(upper) <= 0 && radialSquare.CompareTo(allowed * allowed) <= 0)
            return I.Point(R.Zero);
        I best = default;
        bool first = true;
        foreach (var residual in MeridianResiduals(shape, x, y, z))
        {
            I distance = Sqrt(Square(residual.X) + Square(residual.Y));
            best = first ? distance : new(R.Min(best.Lower, distance.Lower), R.Min(best.Upper, distance.Upper));
            first = false;
        }
        return best;
    }

    static IEnumerable<(I Radial, I X, I Y)> MeridianResiduals(ConvexQueryShape shape, R x, R y, R z)
    {
        R radius = R.From(shape.Radius), lower = R.From(shape.LowerY), ring = R.From(shape.RingY), upper = R.From(shape.UpperY);
        I radial = Root(x * x + z * z), yi = I.Point(y);
        (R X, R Y)[] polygon = [(R.Zero, lower), (radius, ring), (radius, upper), (R.Zero, upper)];
        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
            R dx = b.X - a.X, dy = b.Y - a.Y;
            I fraction = Divide((radial - I.Point(a.X)) * dx + (yi - I.Point(a.Y)) * dy, dx * dx + dy * dy);
            fraction = new(R.Max(R.Zero, R.Min(R.One, fraction.Lower)), R.Max(R.Zero, R.Min(R.One, fraction.Upper)));
            I rx = radial - (I.Point(a.X) + fraction * dx);
            I ry = yi - (I.Point(a.Y) + fraction * dy);
            yield return (radial, rx, ry);
        }
    }

    // A high-precision meridian residual proposes a rational direction. Only the independent
    // complete support plane over every target vertex can prove separation, at any distance.
    static R[] MeridianDirection(in ConvexQueryShape shape, R[] p)
    {
        (I Radial, I X, I Y) best = default;
        I distance = default;
        bool first = true;
        foreach (var residual in MeridianResiduals(shape, p[0], p[1], p[2]))
        {
            I square = Square(residual.X) + Square(residual.Y);
            if (first || square.Upper.CompareTo(distance.Upper) < 0) { best = residual; distance = square; }
            first = false;
        }
        R Mid(I value) => (value.Lower + value.Upper) / new R(2, 1);
        R radial = Mid(best.Radial), x = Mid(best.X), y = Mid(best.Y);
        return radial.N.IsZero ? [R.Zero, y, R.Zero] : [x * p[0] / radial, y, x * p[2] / radial];
    }

    // Exact membership for represented witnesses. This intentionally does not repair a rounded
    // off-surface witness or accept a near-zero normal-cone residual with an epsilon.
    internal static bool NormalConeContains(in ConvexQueryShape shape, Vector3 queryWitness, Vector3 queryOutwardNormal)
    {
        if (shape.Margin != 0) return MarginNormalConeContains(shape, queryWitness, queryOutwardNormal);
        R x = R.From(queryWitness.X), y = R.From(queryWitness.Y), z = R.From(queryWitness.Z);
        R nx = R.From(queryOutwardNormal.X), ny = R.From(queryOutwardNormal.Y), nz = R.From(queryOutwardNormal.Z);
        R radius = R.From(shape.Radius), lower = R.From(shape.LowerY), ring = R.From(shape.RingY), upper = R.From(shape.UpperY);
        R radialSquare = x * x + z * z;
        R allowed = y.CompareTo(ring) < 0 ? radius * (y - lower) / (ring - lower) : radius;
        if (y.CompareTo(lower) < 0 || y.CompareTo(upper) > 0 || radialSquare.CompareTo(allowed * allowed) > 0) return false;
        R attained = x * nx + y * ny + z * nz;
        if ((attained - lower * ny).N.Sign < 0) return false;
        foreach (R height in new[] { ring, upper })
        {
            R gap = attained - height * ny;
            if (gap.N.Sign < 0 || (gap * gap - radius * radius * (nx * nx + nz * nz)).N.Sign < 0) return false;
        }
        return true;
    }

    static I Divide(I value, R divisor) => new(value.Lower / divisor, value.Upper / divisor);
    static I Square(I value)
    {
        R a = value.Lower * value.Lower, b = value.Upper * value.Upper;
        return new(value.Lower.N.Sign <= 0 && value.Upper.N.Sign >= 0 ? R.Zero : R.Min(a, b), R.Max(a, b));
    }
    static I Sqrt(I value) => new(Root(value.Lower).Lower, Root(value.Upper).Upper);
}
