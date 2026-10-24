using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Physics;

internal static partial class ShortBodyShellOracle
{
    // Copy only raw integer coefficients. All membership and support signs are recomputed
    // in the oracle's independent quadratic field, before either vector is rounded to float.
    internal static bool InitialConesContain(in ConvexQueryShape shape, Pose pose, ConvexOracleTarget target,
        BigInteger[] point, BigInteger[] direction)
    {
        R d = R.Zero;
        foreach (var terms in new[] { point, direction })
            for (int i = 0; i < 18; i += 6)
            {
                if (terms[i + 2].IsZero) continue;
                R field = new(terms[i + 4], terms[i + 5]);
                if (!d.N.IsZero && d.CompareTo(field) != 0) return false;
                d = field;
            }
        S C(R value) => new(value, R.Zero, d);
        S Read(BigInteger[] terms, int index) => new(new(terms[index], terms[index + 1]), new(terms[index + 2], terms[index + 3]), d);
        S[] world = [Read(point, 0), Read(point, 6), Read(point, 12)];
        S x = world[0] - C(R.From(pose.Position.X)), y = world[1] - C(R.From(pose.Position.Y)), z = world[2] - C(R.From(pose.Position.Z));
        S nx = Read(direction, 0), ny = Read(direction, 6), nz = Read(direction, 12);
        R lower = R.From(shape.LowerY), ring = R.From(shape.RingY), upper = R.From(shape.UpperY), radius = R.From(shape.Radius);
        S allowed = (y - C(ring)).Sign < 0 ? (y - C(lower)) * (radius / (ring - lower)) : C(radius);
        if ((y - C(lower)).Sign < 0 || (C(upper) - y).Sign < 0 || (allowed * allowed - x * x - z * z).Sign < 0 ||
            (nx * nx + ny * ny + nz * nz).Sign <= 0) return false;
        S attained = x * nx + y * ny + z * nz;
        if ((attained - ny * lower).Sign < 0) return false;
        foreach (R height in new[] { ring, upper })
        {
            S gap = attained - ny * height;
            if (gap.Sign < 0 || (gap * gap - (nx * nx + nz * nz) * (radius * radius)).Sign < 0) return false;
        }
        R[] Vertex(int i) => [new(target.Vertices[i].X.Numerator, target.Vertices[i].X.Denominator),
            new(target.Vertices[i].Y.Numerator, target.Vertices[i].Y.Denominator), new(target.Vertices[i].Z.Numerator, target.Vertices[i].Z.Denominator)];
        for (int i = 0; i < target.Vertices.Length; i++)
        {
            R[] v = Vertex(i);
            if (((C(v[0]) - world[0]) * nx + (C(v[1]) - world[1]) * ny + (C(v[2]) - world[2]) * nz).Sign < 0) return false;
        }
        foreach (var face in target.Faces)
        {
            R[] a = Vertex(face[0]), b = Vertex(face[1]), c = Vertex(face[^1]);
            R[] u = [b[0] - a[0], b[1] - a[1], b[2] - a[2]], v = [c[0] - a[0], c[1] - a[1], c[2] - a[2]];
            if (((world[0] - C(a[0])) * (u[1] * v[2] - u[2] * v[1]) +
                (world[1] - C(a[1])) * (u[2] * v[0] - u[0] * v[2]) +
                (world[2] - C(a[2])) * (u[0] * v[1] - u[1] * v[0])).Sign > 0) return false;
        }
        return true;
    }

    internal static bool TargetSupports(ConvexOracleTarget target, Vector3 point, Vector3 towardTarget)
    {
        R[] p = [R.From(point.X), R.From(point.Y), R.From(point.Z)];
        R[] n = [R.From(towardTarget.X), R.From(towardTarget.Y), R.From(towardTarget.Z)];
        foreach (var v in target.Vertices)
            if (((new R(v.X.Numerator, v.X.Denominator) - p[0]) * n[0] +
                (new R(v.Y.Numerator, v.Y.Denominator) - p[1]) * n[1] +
                (new R(v.Z.Numerator, v.Z.Denominator) - p[2]) * n[2]).N.Sign < 0) return false;
        return towardTarget != Vector3.Zero;
    }

    internal static bool StrictCommonPoint(in ConvexQueryShape shape, Pose pose, ConvexOracleTarget target,
        BigInteger xn, BigInteger xd, BigInteger yn, BigInteger yd, BigInteger zn, BigInteger zd)
    {
        R[] world = [new(xn, xd), new(yn, yd), new(zn, zd)];
        R x = world[0] - R.From(pose.Position.X), y = world[1] - R.From(pose.Position.Y), z = world[2] - R.From(pose.Position.Z);
        R lower = R.From(shape.LowerY), ring = R.From(shape.RingY), upper = R.From(shape.UpperY), radius = R.From(shape.Radius);
        R allowed = y.CompareTo(ring) < 0 ? radius * (y - lower) / (ring - lower) : radius;
        if (y.CompareTo(lower) <= 0 || y.CompareTo(upper) >= 0 || (allowed * allowed - x * x - z * z).N.Sign <= 0) return false;
        R[] Vertex(int i) => [new(target.Vertices[i].X.Numerator, target.Vertices[i].X.Denominator),
            new(target.Vertices[i].Y.Numerator, target.Vertices[i].Y.Denominator), new(target.Vertices[i].Z.Numerator, target.Vertices[i].Z.Denominator)];
        foreach (var face in target.Faces)
        {
            R[] a = Vertex(face[0]), b = Vertex(face[1]), c = Vertex(face[^1]);
            R[] u = [b[0] - a[0], b[1] - a[1], b[2] - a[2]], v = [c[0] - a[0], c[1] - a[1], c[2] - a[2]];
            R[] n = [u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0]];
            if ((n[0] * (world[0] - a[0]) + n[1] * (world[1] - a[1]) + n[2] * (world[2] - a[2])).N.Sign >= 0) return false;
        }
        return target.Faces.Length >= 4;
    }

    // For nonzero n a supporting point of K+mB has the unique offset m*n/|n|.
    // Remove that offset exactly, then prove membership and all three base support inequalities.
    // The test oracle's quadratic field is independent of the production predicates.
    static bool MarginNormalConeContains(in ConvexQueryShape shape, Vector3 witness, Vector3 normal)
    {
        R nx = R.From(normal.X), ny = R.From(normal.Y), nz = R.From(normal.Z);
        R d = nx * nx + ny * ny + nz * nz, m = R.From(shape.Margin);
        if (d.N.IsZero) return MarginMember(shape, witness);
        S C(R a) => new(a, R.Zero, d);
        S x = new(R.From(witness.X), -m * nx / d, d);
        S y = new(R.From(witness.Y), -m * ny / d, d);
        S z = new(R.From(witness.Z), -m * nz / d, d);
        R radius = R.From(shape.Radius), lower = R.From(shape.LowerY), ring = R.From(shape.RingY), upper = R.From(shape.UpperY);
        if ((y - C(lower)).Sign < 0 || (C(upper) - y).Sign < 0) return false;
        S allowed = (y - C(ring)).Sign < 0 ? (y - C(lower)) * (radius / (ring - lower)) : C(radius);
        if ((allowed * allowed - x * x - z * z).Sign < 0) return false;
        S attained = x * nx + y * ny + z * nz;
        if ((attained - C(lower * ny)).Sign < 0) return false;
        foreach (R h in new[] { ring, upper })
        {
            S gap = attained - C(h * ny);
            if (gap.Sign < 0 || (gap * gap - C(radius * radius * (nx * nx + nz * nz))).Sign < 0) return false;
        }
        return true;
    }

    // Compare each closed meridian feature's squared distance to m^2 without a rounded root.
    // Zero normals require actual grown membership too, including a point on its boundary.
    static bool MarginMember(in ConvexQueryShape shape, Vector3 witness)
    {
        R x = R.From(witness.X), y = R.From(witness.Y), z = R.From(witness.Z);
        R d = x * x + z * z, r = R.From(shape.Radius), l = R.From(shape.LowerY), c = R.From(shape.RingY), u = R.From(shape.UpperY);
        R m = R.From(shape.Margin), m2 = m * m, h = c - l;
        R allowed = y.CompareTo(c) < 0 ? r * (y - l) / h : r;
        if (y.CompareTo(l) >= 0 && y.CompareTo(u) <= 0 && d.CompareTo(allowed * allowed) <= 0) return true;
        if ((d + (y - l) * (y - l) - m2).N.Sign <= 0) return true;
        S C(R a) => new(a, R.Zero, d);
        S rho = new(R.Zero, R.One, d);
        S fraction = (rho * r + C(h * (y - l))) * (R.One / (r * r + h * h));
        if (fraction.Sign >= 0 && (C(R.One) - fraction).Sign >= 0)
        {
            S squared = (C(h * h * d + r * r * (y - l) * (y - l)) - rho * (new R(2, 1) * r * h * (y - l))) *
                (R.One / (r * r + h * h));
            if ((squared - C(m2)).Sign <= 0) return true;
        }
        S radialSquared = C(d + r * r) - rho * (new R(2, 1) * r);
        if (y.CompareTo(c) >= 0 && y.CompareTo(u) <= 0 && (radialSquared - C(m2)).Sign <= 0) return true;
        if (d.CompareTo(r * r) <= 0 && ((y - u) * (y - u) - m2).N.Sign <= 0) return true;
        foreach (R height in new[] { c, u })
            if ((radialSquared + C((y - height) * (y - height) - m2)).Sign <= 0) return true;
        return false;
    }

    readonly record struct S(R A, R B, R D)
    {
        internal int Sign
        {
            get
            {
                if (B.N.IsZero || D.N.IsZero) return A.N.Sign;
                if (A.N.IsZero || A.N.Sign == B.N.Sign) return B.N.Sign;
                return A.N.Sign * (A * A - B * B * D).N.Sign;
            }
        }
        public static S operator +(S a, S b) => new(a.A + b.A, a.B + b.B, a.D);
        public static S operator -(S a, S b) => new(a.A - b.A, a.B - b.B, a.D);
        public static S operator *(S a, S b) => new(a.A * b.A + a.B * b.B * a.D, a.A * b.B + a.B * b.A, a.D);
        public static S operator *(S a, R b) => new(a.A * b, a.B * b, a.D);
    }
}
