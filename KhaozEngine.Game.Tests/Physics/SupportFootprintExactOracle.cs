using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;
using V = KhaozEngine.Tests.Physics.InstalledPoseOracle.V;

namespace KhaozEngine.Tests.Physics;

/// <summary>Independent rational polygon strata over installed binary32 source values. Irrational roots use
/// integer-square-root floor and ceiling at the oracle's fixed 112-bit binary scale. No production predicate,
/// interval, transform or extremum helper supplies a reference.</summary>
internal static class SupportFootprintExactOracle
{
    internal readonly record struct Bounds(R Lower, R Upper)
    {
        internal static Bounds Exact(R value) => new(value, value);
        internal double Middle => AsDouble((Lower + Upper) / new R(2, 1));
        internal static Bounds Root(R value)
        {
            var root = InstalledPoseOracle.Root(value);
            return new(root.Lower, root.Upper);
        }
        public static Bounds operator +(Bounds a, Bounds b) => new(a.Lower + b.Lower, a.Upper + b.Upper);
        public static Bounds operator -(Bounds a) => new(-a.Upper, -a.Lower);
        public static Bounds operator -(Bounds a, Bounds b) => a + -b;
        public static Bounds operator *(Bounds a, Bounds b)
        {
            R[] products = [a.Lower * b.Lower, a.Lower * b.Upper, a.Upper * b.Lower, a.Upper * b.Upper];
            return new(products.Aggregate(Min), products.Aggregate(Max));
        }
        public static Bounds operator /(Bounds a, Bounds b)
        {
            if (b.Lower <= R.Zero && b.Upper >= R.Zero) throw new InvalidOperationException("Oracle divisor contains zero.");
            return a * new Bounds(R.One / b.Upper, R.One / b.Lower);
        }
        public static implicit operator Bounds(R value) => Exact(value);
    }

    internal readonly record struct Point(Bounds X, Bounds Y, Bounds Z)
    {
        internal static Point Exact(V value) => new(value.X, value.Y, value.Z);
        internal OracleVector Middle => new(X.Middle, Y.Middle, Z.Middle);
    }

    internal readonly record struct Extrema(Bounds Minimum, Bounds Maximum, Point Witness);
    internal static double AsDouble(R value) => (double)value.Numerator / (double)value.Denominator;
    static R Min(R a, R b) => a < b ? a : b;
    static R Max(R a, R b) => a > b ? a : b;
    static int Sign(R value) => value.Numerator.Sign;
    static V From(OracleVector point) => new(R.From(point.X), R.From(point.Y), R.From(point.Z));
    internal static V[] Vertices(OracleFace face) => face.ExactVertices ?? face.Vertices.Select(From).ToArray();
    internal static V Normal(V[] vertices) => V.Cross(vertices[1] - vertices[0], vertices[2] - vertices[0]);
    internal static Bounds Plane(OracleFace face, double x, double z)
    {
        V[] vertices = Vertices(face);
        return Bounds.Exact(Plane(vertices[0], Normal(vertices), R.From(x), R.From(z)));
    }
    static R Plane(V first, V normal, R x, R z) => first.Y +
        (normal.X * (first.X - x) + normal.Z * (first.Z - z)) / normal.Y;

    internal static V[] Transform(Vector3[] local, Pose pose)
    {
        var matrix = InstalledPoseOracle.Represented(pose.Orientation);
        V translation = V.From(pose.Position);
        return local.Select(p => matrix.Apply(V.From(p)) + translation).ToArray();
    }

    // The exact sign of k+l*sqrt(d), including equality, is reduced to rational squares.
    static int Radical(R k, R l, R d)
    {
        if (l == R.Zero || d == R.Zero) return Sign(k);
        if (k == R.Zero || Sign(k) == Sign(l)) return Sign(l);
        int comparison = Sign(k.Square() - l.Square() * d);
        return comparison == 0 ? 0 : comparison > 0 ? Sign(k) : Sign(l);
    }

    static R SquareXZ(V value) => value.X.Square() + value.Z.Square();
    static R DotXZ(V a, V b) => a.X * b.X + a.Z * b.Z;
    static Point Along(V vertex, V edge, Bounds t) => new(
        (Bounds)vertex.X + (Bounds)edge.X * t, (Bounds)vertex.Y + (Bounds)edge.Y * t,
        (Bounds)vertex.Z + (Bounds)edge.Z * t);

    internal static IReadOnlyList<Bounds> EdgeCircleRoots(V vertex, V edge, V axis, R radius)
    {
        R a = SquareXZ(edge), b = DotXZ(vertex - axis, edge);
        if (a == R.Zero) return [];
        R discriminant = b.Square() - a * (SquareXZ(vertex - axis) - radius.Square());
        if (discriminant < R.Zero) return [];
        var roots = new List<Bounds>();
        foreach (int sign in new[] { -1, 1 })
        {
            R direction = new(sign, 1);
            if (Radical(-b, direction, discriminant) < 0 || Radical(a + b, -direction, discriminant) < 0) continue;
            Bounds t = ((Bounds)(-b) + (Bounds)direction * Bounds.Root(discriminant)) / (Bounds)a;
            // Algebraic equality belongs to the closed edge, independent of root enclosure rounding.
            if (Radical(-b, direction, discriminant) == 0) t = Bounds.Exact(R.Zero);
            if (Radical(a + b, -direction, discriminant) == 0) t = Bounds.Exact(R.One);
            roots.Add(t);
            if (discriminant == R.Zero) break;
        }
        return roots;
    }

    static bool Inside(V[] vertices, V normal, V axis, V direction, R radius, R squared)
    {
        for (int i = 0; i < vertices.Length; i++)
        {
            V edge = vertices[(i + 1) % vertices.Length] - vertices[i], delta = axis - vertices[i];
            R k = edge.Z * delta.X - edge.X * delta.Z;
            R l = radius * (edge.Z * direction.X - edge.X * direction.Z);
            if (Radical(l, k, squared) * Sign(normal.Y) < 0) return false;
        }
        return true;
    }

    internal static Extrema? Disc(OracleFace face, double x, double z, R radius)
    {
        V[] vertices = Vertices(face);
        V normal = Normal(vertices), axis = new(R.From(x), R.Zero, R.From(z));
        var candidates = new List<Point>();
        foreach (V vertex in vertices)
            if (SquareXZ(vertex - axis) <= radius.Square()) candidates.Add(Point.Exact(vertex));
        for (int i = 0; i < vertices.Length; i++)
        {
            V edge = vertices[(i + 1) % vertices.Length] - vertices[i];
            foreach (Bounds t in EdgeCircleRoots(vertices[i], edge, axis, radius)) candidates.Add(Along(vertices[i], edge, t));
        }
        if (normal.Y != R.Zero)
        {
            if (Inside(vertices, normal, axis, V.Zero, radius, R.One))
                candidates.Add(Point.Exact(new(axis.X, Plane(vertices[0], normal, axis.X, axis.Z), axis.Z)));
            V direction = new(-normal.X * new R(Sign(normal.Y), 1), R.Zero, -normal.Z * new R(Sign(normal.Y), 1));
            R squared = SquareXZ(direction);
            if (squared > R.Zero)
                foreach (int sign in new[] { -1, 1 })
                {
                    V d = direction * new R(sign, 1);
                    if (!Inside(vertices, normal, axis, d, radius, squared)) continue;
                    Bounds scale = (Bounds)radius / Bounds.Root(squared);
                    Bounds px = (Bounds)axis.X + (Bounds)d.X * scale, pz = (Bounds)axis.Z + (Bounds)d.Z * scale;
                    Bounds py = (Bounds)vertices[0].Y + ((Bounds)normal.X * ((Bounds)vertices[0].X - px) +
                        (Bounds)normal.Z * ((Bounds)vertices[0].Z - pz)) / (Bounds)normal.Y;
                    candidates.Add(new(px, py, pz));
                }
        }
        if (candidates.Count == 0) return null;
        Point witness = candidates.MaxBy(p => p.Y.Middle);
        return new(new(candidates.Select(p => p.Y.Lower).Aggregate(Min), candidates.Select(p => p.Y.Upper).Aggregate(Min)),
            new(candidates.Select(p => p.Y.Lower).Aggregate(Max), candidates.Select(p => p.Y.Upper).Aggregate(Max)), witness);
    }

    internal static Bounds? Contact(OracleFace face, in SupportFootprintProposal proposal)
    {
        V[] vertices = Vertices(face);
        V normal = Normal(vertices);
        if (normal.Y <= R.Zero) return null;
        V axis = new(R.From(proposal.Start.Position.X), R.Zero, R.From(proposal.Start.Position.Z));
        R radius = R.From(proposal.Probe.Radius);
        var candidates = new List<Bounds>();
        foreach (V vertex in vertices)
        {
            R squared = radius.Square() - SquareXZ(vertex - axis);
            if (squared >= R.Zero) candidates.Add((Bounds)vertex.Y + Bounds.Root(squared) - (Bounds)radius);
        }
        for (int i = 0; i < vertices.Length; i++)
        {
            V vertex = vertices[i], edge = vertices[(i + 1) % vertices.Length] - vertex;
            R a = SquareXZ(edge);
            if (a == R.Zero) continue;
            R b = DotXZ(vertex - axis, edge), cross = (vertex.X - axis.X) * edge.Z - (vertex.Z - axis.Z) * edge.X;
            R k = radius.Square() - cross.Square() / a;
            if (k < R.Zero) continue;
            foreach (Bounds root in EdgeCircleRoots(vertex, edge, axis, radius))
                candidates.Add(Along(vertex, edge, root).Y - (Bounds)radius);
            R d = k / (a * (a + edge.Y.Square())), centre = -b / a;
            if (Radical(centre, edge.Y, d) < 0 || Radical(R.One - centre, -edge.Y, d) < 0) continue;
            // The stationary edge height simplifies without evaluating its correlated horizontal squares.
            candidates.Add((Bounds)(vertex.Y + edge.Y * centre - radius) + Bounds.Root(k * (a + edge.Y.Square()) / a));
        }
        R lengthSquared = normal.LengthSquared;
        V direction = new(-normal.X, R.Zero, -normal.Z);
        if (Inside(vertices, normal, axis, direction, radius, lengthSquared))
            candidates.Add((Bounds)(Plane(vertices[0], normal, axis.X, axis.Z) - radius) +
                (Bounds)radius * Bounds.Root(lengthSquared) / (Bounds)normal.Y);
        return candidates.Count == 0 ? null : new(candidates.Select(p => p.Lower).Aggregate(Max),
            candidates.Select(p => p.Upper).Aggregate(Max));
    }
}
