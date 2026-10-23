using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Physics;

internal static partial class ShortBodyShellOracle
{
    // Cartesian boxes admit a separate piecewise meridian ray construction. No production
    // feature, normal-cone, polynomial or root function supplies any expected event.
    static ConvexOracleEvent SweepEntry(in ConvexQueryShape shape, Pose pose, ConvexOracleTarget target, Vector3 displacement)
    {
        if (!float.IsFinite(displacement.X) || !float.IsFinite(displacement.Y) || !float.IsFinite(displacement.Z)) return default;
        R[] d = [R.From(displacement.X), R.From(displacement.Y), R.From(displacement.Z)];
        I length = Root(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
        if (ClassifySweepPoint(shape, At(target, pose, d, R.Zero), d, out _) == 1)
            return new(true, 0, 0, ConvexSweepStatus.Hit);
        if (shape.Margin == 0 && Cartesian(target, pose, out var low, out var high))
            return CartesianSweep(shape, low, high, d, length);
        return BracketSweep(shape, pose, target, d, length);
    }

    static bool Cartesian(ConvexOracleTarget target, Pose pose, out R[] low, out R[] high)
    {
        low = new R[3]; high = new R[3];
        if (target.Vertices.Length != 8) return false;
        var points = new R[8][];
        for (int i = 0; i < 8; i++)
        {
            var p = target.Vertices[i];
            points[i] = [new R(p.X.Numerator, p.X.Denominator) - R.From(pose.Position.X),
                new R(p.Y.Numerator, p.Y.Denominator) - R.From(pose.Position.Y),
                new R(p.Z.Numerator, p.Z.Denominator) - R.From(pose.Position.Z)];
            for (int j = 0; j < 3; j++)
            { low[j] = i == 0 ? points[i][j] : R.Min(low[j], points[i][j]); high[j] = i == 0 ? points[i][j] : R.Max(high[j], points[i][j]); }
        }
        int mask = 0;
        foreach (var p in points)
        {
            int corner = 0;
            for (int j = 0; j < 3; j++)
                if (p[j].CompareTo(high[j]) == 0) corner |= 1 << j;
                else if (p[j].CompareTo(low[j]) != 0) return false;
            if ((mask & (1 << corner)) != 0) return false;
            mask |= 1 << corner;
        }
        return mask == 255;
    }

    readonly record struct Affine(R A, R B)
    {
        internal R At(R t) => A + B * t;
        public static Affine operator -(Affine a, Affine b) => new(a.A - b.A, a.B - b.B);
    }

    static ConvexOracleEvent CartesianSweep(in ConvexQueryShape shape, R[] low, R[] high, R[] d, I length)
    {
        var cuts = new List<R> { R.Zero, R.One };
        R lower = R.From(shape.LowerY), ring = R.From(shape.RingY), upper = R.From(shape.UpperY);
        for (int j = 0; j < 3; j++)
        {
            if (d[j].N.IsZero) continue;
            foreach (R bound in new[] { low[j], high[j] })
                foreach (R level in j == 1 ? new[] { lower, ring, upper } : new[] { R.Zero })
                {
                    R t = SignedDivide(bound - level, d[j]);
                    if (t.CompareTo(R.Zero) > 0 && t.CompareTo(R.One) < 0) cuts.Add(t);
                }
        }
        cuts.Sort();
        for (int cell = 0; cell + 1 < cuts.Count; cell++)
        {
            R from = cuts[cell], to = cuts[cell + 1];
            if (from.CompareTo(to) == 0) continue;
            if (CartesianMember(shape, low, high, d, from)) return SweepBounds(I.Point(from), length);
            R middle = (from + to) / new R(2, 1);
            Affine Near(int axis) => (low[axis] - d[axis] * middle).N.Sign > 0 ? new(low[axis], -d[axis]) :
                (high[axis] - d[axis] * middle).N.Sign < 0 ? new(high[axis], -d[axis]) : new(R.Zero, R.Zero);
            Affine x = Near(0), z = Near(2);
            Affine y = (low[1] - d[1] * middle).CompareTo(ring) > 0 ? new(low[1], -d[1]) :
                (high[1] - d[1] * middle).CompareTo(ring) < 0 ? new(high[1], -d[1]) : new(ring, R.Zero);
            if (y.At(middle).CompareTo(lower) < 0 || y.At(middle).CompareTo(upper) > 0) continue;
            bool cone = y.At(middle).CompareTo(ring) < 0;
            Affine height = y - new Affine(lower, R.Zero);
            R r2 = R.From(shape.Radius) * R.From(shape.Radius), h2 = (ring - lower) * (ring - lower);
            R scale = cone ? h2 : R.One;
            R aa = scale * (x.B * x.B + z.B * z.B) - (cone ? r2 * height.B * height.B : R.Zero);
            R bb = new R(2, 1) * (scale * (x.A * x.B + z.A * z.B) - (cone ? r2 * height.A * height.B : R.Zero));
            R cc = scale * (x.A * x.A + z.A * z.A) - r2 * (cone ? height.A * height.A : R.One);
            R F(R t) => (aa * t + bb) * t + cc;
            if (F(from).N.Sign <= 0) return SweepBounds(I.Point(from), length);
            foreach (I root in OracleRoots(aa, bb, cc))
            {
                if (root.Upper.CompareTo(from) < 0 || root.Lower.CompareTo(to) > 0) continue;
                if (root.Lower.CompareTo(from) < 0 || root.Upper.CompareTo(to) > 0)
                {
                    if (F(from).N.IsZero) return SweepBounds(I.Point(from), length);
                    if (F(to).N.IsZero) return SweepBounds(I.Point(to), length);
                    return default;
                }
                return SweepBounds(root, length);
            }
        }
        if (CartesianMember(shape, low, high, d, R.One)) return SweepBounds(I.Point(R.One), length);
        return new(true, Down(length.Lower), -Down(-length.Upper), ConvexSweepStatus.Clear);
    }

    static bool CartesianMember(in ConvexQueryShape shape, R[] low, R[] high, R[] d, R t)
    {
        R Near(int axis)
        {
            R a = low[axis] - d[axis] * t, b = high[axis] - d[axis] * t;
            return a.N.Sign > 0 ? a : b.N.Sign < 0 ? b : R.Zero;
        }
        R y = R.Max(low[1] - d[1] * t, R.Min(high[1] - d[1] * t, R.From(shape.RingY)));
        R lower = R.From(shape.LowerY), upper = R.From(shape.UpperY);
        if (y.CompareTo(lower) < 0 || y.CompareTo(upper) > 0) return false;
        R radius = R.From(shape.Radius), x = Near(0), z = Near(2);
        if (y.CompareTo(R.From(shape.RingY)) < 0) radius = radius * (y - lower) / (R.From(shape.RingY) - lower);
        return (radius * radius - x * x - z * z).N.Sign >= 0;
    }

    static IEnumerable<I> OracleRoots(R a, R b, R c)
    {
        if (a.N.IsZero)
        { if (!b.N.IsZero) yield return I.Point(SignedDivide(-c, b)); yield break; }
        R discriminant = b * b - new R(4, 1) * a * c;
        if (discriminant.N.Sign < 0) yield break;
        I root = Root(discriminant);
        I first = SignedDivide(I.Point(-b) - root, new R(2, 1) * a);
        I second = SignedDivide(I.Point(-b) + root, new R(2, 1) * a);
        yield return a.N.Sign > 0 ? first : second;
        if (!discriminant.N.IsZero) yield return a.N.Sign > 0 ? second : first;
    }

    static R SignedDivide(R a, R b) => b.N.Sign < 0 ? (-a) / (-b) : a / b;
    static I SignedDivide(I a, R b) => b.N.Sign < 0
        ? new(SignedDivide(a.Upper, b), SignedDivide(a.Lower, b)) : new(a.Lower / b, a.Upper / b);
    static ConvexOracleEvent SweepBounds(I t, I length)
    {
        var bounds = Outward(new(t.Lower * length.Lower, t.Upper * length.Upper));
        return new(true, bounds.Lower, bounds.Upper, ConvexSweepStatus.Hit);
    }

    static ConvexOracleTarget At(ConvexOracleTarget target, Pose pose, R[] d, R t)
    {
        var points = new InstalledPoseOracle.V[target.Vertices.Length];
        for (int i = 0; i < points.Length; i++)
        {
            var p = target.Vertices[i];
            R x = new R(p.X.Numerator, p.X.Denominator) - R.From(pose.Position.X) - d[0] * t;
            R y = new R(p.Y.Numerator, p.Y.Denominator) - R.From(pose.Position.Y) - d[1] * t;
            R z = new R(p.Z.Numerator, p.Z.Denominator) - R.From(pose.Position.Z) - d[2] * t;
            points[i] = new(new(x.N, x.D), new(y.N, y.D), new(z.N, z.D));
        }
        return new(points, target.Faces);
    }

    static ConvexOracleEvent BracketSweep(in ConvexQueryShape shape, Pose pose, ConvexOracleTarget target, R[] d, I length)
    {
        R lo = R.Zero, hi = R.One;
        bool known = false;
        // Samples only propose an attained common point. Their absence is never a Clear proof.
        for (int i = 1; i <= 32; i++)
        {
            R time = new(i, 32);
            int state = ClassifySweepPoint(shape, At(target, pose, d, time), d, out bool first);
            if (state != 1) continue;
            if (first) return SweepBounds(I.Point(time), length);
            hi = time; known = true; break;
        }
        if (!known)
        {
            var a = At(target, pose, d, R.Zero); var b = At(target, pose, d, R.One);
            var vertices = new InstalledPoseOracle.V[a.Vertices.Length * 2];
            a.Vertices.CopyTo(vertices, 0); b.Vertices.CopyTo(vertices, a.Vertices.Length);
            var swept = GeneralEntry(shape, Pose.Identity, new(vertices, a.Faces));
            return swept.IsComplete && swept.Lower > 0
                ? new(true, Down(length.Lower), -Down(-length.Upper), ConvexSweepStatus.Clear) : default;
        }
        // Convexity makes the contact-time set an interval. Before a known contact, exact
        // static separation therefore proves the entire prefix. No operational miss is used.
        for (int i = 0; i < 48; i++)
        {
            R mid = (lo + hi) / new R(2, 1);
            int state = ClassifySweepPoint(shape, At(target, pose, d, mid), d, out bool first);
            if (state == 0) return default;
            if (state == 1)
            {
                if (first) return SweepBounds(I.Point(mid), length);
                hi = mid;
            }
            else lo = mid;
        }
        return SweepBounds(new(lo, hi), length);
    }

    // Face centroids and the solid centroid are attained rational convex combinations. They
    // are contact proposals only. Complete continuous support planes certify separation.
    static int ClassifySweepPoint(in ConvexQueryShape shape, ConvexOracleTarget target, R[] delta, out bool first)
    {
        first = false;
        var vertices = new R[target.Vertices.Length][];
        for (int i = 0; i < vertices.Length; i++)
        {
            var p = target.Vertices[i];
            vertices[i] = [new(p.X.Numerator, p.X.Denominator), new(p.Y.Numerator, p.Y.Denominator), new(p.Z.Numerator, p.Z.Denominator)];
        }
        var value = shape;
        bool Member(R[] p) => MeridianDistance(value, p[0], p[1], p[2]).Upper.CompareTo(R.From(value.Margin)) <= 0;
        bool contact = Array.Exists(vertices, Member);
        R[] Average(int[] indices)
        {
            R[] p = [R.Zero, R.Zero, R.Zero];
            foreach (int i in indices) for (int axis = 0; axis < 3; axis++) p[axis] += vertices[i][axis];
            for (int axis = 0; axis < 3; axis++) p[axis] /= new R(indices.Length, 1);
            return p;
        }
        foreach (var face in target.Faces) contact |= Member(Average(face));
        var all = new int[vertices.Length];
        for (int i = 0; i < all.Length; i++) all[i] = i;
        contact |= Member(Average(all));
        bool separated = false;
        foreach (var face in target.Faces)
        {
            R[] a = vertices[face[0]], b = vertices[face[1]], c = vertices[face[^1]];
            R[] u = [b[0] - a[0], b[1] - a[1], b[2] - a[2]], v = [c[0] - a[0], c[1] - a[1], c[2] - a[2]];
            R[] n = [u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0]];
            foreach (var direction in new[] { n, new[] { -n[0], -n[1], -n[2] } })
            {
                if (FacePoint(shape, vertices, target.Faces, a, direction, out var point)) contact |= Member(point);
                separated |= SeparationLower(shape, vertices, direction).N.Sign > 0;
                if (!contact || (delta[0] * direction[0] + delta[1] * direction[1] + delta[2] * direction[2]).N.Sign <= 0) continue;
                I radial = Root(direction[0] * direction[0] + direction[2] * direction[2]) * R.From(shape.Radius);
                I apex = I.Point(R.From(shape.LowerY) * direction[1]);
                I shoulder = I.Point(R.From(shape.RingY) * direction[1]) + radial;
                I head = I.Point(R.From(shape.UpperY) * direction[1]) + radial;
                I support = new(R.Max(apex.Lower, R.Max(shoulder.Lower, head.Lower)), R.Max(apex.Upper, R.Max(shoulder.Upper, head.Upper)));
                support += Root(direction[0] * direction[0] + direction[1] * direction[1] + direction[2] * direction[2]) * R.From(shape.Margin);
                R minimum = vertices[0][0] * direction[0] + vertices[0][1] * direction[1] + vertices[0][2] * direction[2];
                foreach (var p in vertices) minimum = R.Min(minimum, p[0] * direction[0] + p[1] * direction[1] + p[2] * direction[2]);
                first |= minimum.CompareTo(support.Lower) == 0 && minimum.CompareTo(support.Upper) == 0;
            }
        }
        if (contact) return 1;
        if (separated) return -1;
        var fallback = Entry(shape, Pose.Identity, target, Vector3.Zero);
        return fallback.IsComplete ? fallback.Upper == 0 ? 1 : fallback.Lower > 0 ? -1 : 0 : 0;
    }

    static bool FacePoint(in ConvexQueryShape shape, R[][] vertices, int[][] faces, R[] a, R[] n, out R[] point)
    {
        point = [];
        R norm = n[0] * n[0] + n[1] * n[1] + n[2] * n[2];
        if (norm.N.IsZero) return false;
        I radial = Root(n[0] * n[0] + n[2] * n[2]);
        if (radial.Lower.CompareTo(radial.Upper) != 0) return false;
        R r = R.From(shape.Radius), s = R.From(shape.LowerY), c = R.From(shape.RingY), t = R.From(shape.UpperY);
        R height = n[1].N.Sign > 0 ? t : n[1].N.Sign < 0 ? c : (c + t) / new R(2, 1);
        R radius = r;
        R advantage = (c - s) * n[1] + r * radial.Lower;
        if (advantage.N.Sign < 0) { height = s; radius = R.Zero; }
        else if (advantage.N.IsZero && n[1].N.Sign < 0) { height = (s + c) / new R(2, 1); radius /= new R(2, 1); }
        if (radial.Lower.N.IsZero) { height = n[1].N.Sign > 0 ? t : s; radius = R.Zero; }
        R[] q = [radius.N.IsZero ? R.Zero : radius * n[0] / radial.Lower, height,
            radius.N.IsZero ? R.Zero : radius * n[2] / radial.Lower];
        R gap = ((a[0] - q[0]) * n[0] + (a[1] - q[1]) * n[1] + (a[2] - q[2]) * n[2]) / norm;
        point = [q[0] + n[0] * gap, q[1] + n[1] * gap, q[2] + n[2] * gap];
        R[] center = [R.Zero, R.Zero, R.Zero];
        foreach (var v in vertices) for (int j = 0; j < 3; j++) center[j] += v[j];
        for (int j = 0; j < 3; j++) center[j] /= new R(vertices.Length, 1);
        foreach (var face in faces)
        {
            R[] p = vertices[face[0]], b = vertices[face[1]], e = vertices[face[^1]];
            R[] u = [b[0] - p[0], b[1] - p[1], b[2] - p[2]], v = [e[0] - p[0], e[1] - p[1], e[2] - p[2]];
            R[] normal = [u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0]];
            R At(R[] x) => (x[0] - p[0]) * normal[0] + (x[1] - p[1]) * normal[1] + (x[2] - p[2]) * normal[2];
            int inside = At(center).N.Sign;
            if (inside == 0 || At(point).N.Sign * inside < 0) return false;
        }
        return true;
    }
}
