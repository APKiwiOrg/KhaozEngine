using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Physics;

internal static partial class ShortBodyShellOracle
{
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
