using System;
using System.Numerics;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Independent finite arithmetic. No production transform, classifier, ray or private method is used.
// M stores columns, unlike Bepu's row-basis field names. All arithmetic below is rational.
internal static class InstalledPoseOracle
{
    internal static readonly R PositionCeiling = new(1, 4000);
    internal static readonly R WidthCeiling = new(1, 10000);
    internal static readonly R NormalCeiling = new(1, 100000);
    internal static readonly R Tau = new(1, 1 << 20);
    internal static R Norm(Quaternion q) => R.From(q.X).Square() + R.From(q.Y).Square() +
        R.From(q.Z).Square() + R.From(q.W).Square();
    internal static bool InBand(Quaternion q) => Norm(q) >= R.One - Tau && Norm(q) <= R.One + Tau;

    // The real polynomial, its represented coefficient graph and normalized ideal rotation differ.
    // NormalizedIdeal uses I+(A-I)/s, so no approximate square root enters that distinction.
    internal static M Real(Quaternion q) => Coefficients(q, false);
    internal static M Represented(Quaternion q) => Coefficients(q, true);
    internal static M NormalizedIdeal(Quaternion q)
    {
        M a = Real(q);
        R s = Norm(q);
        return new(V.UnitX + (a.X - V.UnitX) / s, V.UnitY + (a.Y - V.UnitY) / s,
            V.UnitZ + (a.Z - V.UnitZ) / s);
    }

    static M Coefficients(Quaternion q, bool rounded)
    {
        R Eval(R value) => rounded ? Single(value) : value;
        R x = R.From(q.X), y = R.From(q.Y), z = R.From(q.Z), w = R.From(q.W);
        R xx = Eval(Eval(x + x) * x), yy = Eval(Eval(y + y) * y), zz = Eval(Eval(z + z) * z);
        R xy = Eval(Eval(x + x) * y), xz = Eval(Eval(x + x) * z), xw = Eval(Eval(x + x) * w);
        R yz = Eval(Eval(y + y) * z), yw = Eval(Eval(y + y) * w), zw = Eval(Eval(z + z) * w);
        return new(new(Eval(Eval(R.One - yy) - zz), Eval(xy + zw), Eval(xz - yw)),
            new(Eval(xy - zw), Eval(Eval(R.One - xx) - zz), Eval(yz + xw)),
            new(Eval(xz + yw), Eval(yz - xw), Eval(Eval(R.One - xx) - yy)));
    }

    // Each multiply, left-associated add and translation has its own round-to-nearest-even node.
    // These outputs are observations of an operation graph, never a replacement planar vertex set.
    internal static V RoundedPoint(M a, V translation, V local)
    {
        R Dot(R x, R y, R z) => Single(Single(Single(local.X * x) + Single(local.Y * y)) + Single(local.Z * z));
        return new(Single(Dot(a.X.X, a.Y.X, a.Z.X) + translation.X),
            Single(Dot(a.X.Y, a.Y.Y, a.Z.Y) + translation.Y),
            Single(Dot(a.X.Z, a.Y.Z, a.Z.Z) + translation.Z));
    }

    internal static Pose Composed(Pose local, Pose parent)
    {
        R ax = R.From(local.Orientation.X), ay = R.From(local.Orientation.Y);
        R az = R.From(local.Orientation.Z), aw = R.From(local.Orientation.W);
        R bx = R.From(parent.Orientation.X), by = R.From(parent.Orientation.Y);
        R bz = R.From(parent.Orientation.Z), bw = R.From(parent.Orientation.W);
        R Sum(R a, R b, R c, R d) => Single(Single(Single(a + b) + c) - d);
        R x = Sum(Single(aw * bx), Single(ax * bw), Single(az * by), Single(ay * bz));
        R y = Sum(Single(aw * by), Single(ay * bw), Single(ax * bz), Single(az * bx));
        R z = Sum(Single(aw * bz), Single(az * bw), Single(ay * bx), Single(ax * by));
        R w = Single(Single(Single(Single(aw * bw) - Single(ax * bx)) - Single(ay * by)) - Single(az * bz));
        V position = RoundedPoint(Represented(parent.Orientation), V.From(parent.Position), V.From(local.Position));
        return new(position.AsSingle(), new Quaternion(x.AsSingle(), y.AsSingle(), z.AsSingle(), w.AsSingle()));
    }

    internal readonly record struct Face(V Axis, V Geometry, V Outward, R DistanceSquared, bool Front)
    {
        internal I Distance => Root(DistanceSquared);
        internal I Separation => Distance - R.From(0.25f);
    }

    internal static Face BoxTop(M a, V translation, Vector3 proposal)
    {
        Assert.True(a.Determinant > R.Zero, "The finite affine image must preserve convex orientation.");
        V center = V.From(proposal);
        V anchor = a.Apply(V.UnitY) + translation;
        V normal = V.Cross(a.Z, a.X);
        Face face = Project(center, anchor, normal);
        Assert.True(face.Front);
        V local = a.Inverse(face.Geometry - translation);
        Assert.Equal(R.One, local.Y);
        Assert.True(Abs(local.X) < new R(3, 4) && Abs(local.Z) < new R(3, 4),
            "The unique orthogonal witness must lie strictly inside the finite face.");
        // Convexity gives the global minimum. Strict face interior excludes every other stratum.
        foreach (int x in new[] { -1, 1 })
            foreach (int y in new[] { -1, 1 })
                foreach (int z in new[] { -1, 1 })
                    Assert.True(V.Dot(a.Apply(new V(new R(x, 1), new R(y, 1), new R(z, 1))) +
                        translation - anchor, normal) <= R.Zero);
        AssertContactBand(face);
        return face;
    }

    internal static Face Triangle(M a, V translation, Vector3 proposal, bool front)
    {
        Assert.True(a.Determinant > R.Zero);
        V aa = a.Apply(TriangleA) + translation, bb = a.Apply(TriangleB) + translation;
        V cc = a.Apply(TriangleC) + translation;
        // Pinned one-sided convention is Cross(C-A,B-A), not the usual opposite winding.
        Face face = Project(V.From(proposal), aa, V.Cross(cc - aa, bb - aa));
        Assert.Equal(front, face.Front);
        AssertTriangleInterior(face.Geometry, aa, bb, cc);
        AssertContactBand(face);
        return face;
    }

    internal static readonly V TriangleA = V.From(new Vector3(-2, 1, -2));
    internal static readonly V TriangleB = V.From(new Vector3(2, 1, -2));
    internal static readonly V TriangleC = V.From(new Vector3(0, 1, 2));

    // Local-side evidence includes the represented transpose path and point-operation rounding.
    // This is a strict sign/interior certificate, not a copied contact or ray solver.
    internal static void AssertTriangleLocalSide(M represented, V translation, Vector3 proposal, bool front)
    {
        V center = V.From(proposal);
        V difference = center - translation;
        V roundedDifference = new(Single(difference.X), Single(difference.Y), Single(difference.Z));
        M transpose = represented.Transpose;
        V exactLocal = transpose.Apply(difference);
        V roundedLocal = RoundedPoint(transpose, V.Zero, roundedDifference);
        foreach (V local in new[] { exactLocal, roundedLocal })
        {
            R side = V.Dot(local - TriangleA, V.Cross(TriangleC - TriangleA, TriangleB - TriangleA));
            Assert.True(front ? side > R.One : side < -R.One,
                "Forward and pinned represented local-side signs must robustly agree.");
            AssertTriangleInterior(new(local.X, R.One, local.Z), TriangleA, TriangleB, TriangleC);
        }
        // CapsuleTriangleTester also subtracts its binary32 triangle centroid. Enclose that
        // source-side expression separately. Here the centered face normal remains exactly +Y.
        V Round(V value) => new(Single(value.X), Single(value.Y), Single(value.Z));
        V centroid = Round(Round(Round(TriangleA + TriangleB) + TriangleC) * R.From(1f / 3f));
        Assert.Equal(R.One, centroid.Y);
        V workingA = Round(TriangleA - centroid), workingB = Round(TriangleB - centroid);
        V workingC = Round(TriangleC - centroid), workingCenter = Round(roundedLocal - centroid);
        Assert.Equal(V.UnitY * new R(16, 1), V.Cross(workingC - workingA, workingB - workingA));
        Assert.True(front ? workingCenter.Y > new R(1, 8) : workingCenter.Y < new R(-1, 8));
        AssertTriangleInterior(new(workingCenter.X, R.Zero, workingCenter.Z), workingA, workingB, workingC);
    }

    static void AssertTriangleInterior(V point, V a, V b, V c)
    {
        V u = b - a, v = c - a, p = point - a;
        R uu = V.Dot(u, u), uv = V.Dot(u, v), vv = V.Dot(v, v);
        R denominator = uu * vv - uv * uv;
        Assert.True(denominator > R.Zero);
        R alpha = (V.Dot(p, u) * vv - V.Dot(p, v) * uv) / denominator;
        R beta = (V.Dot(p, v) * uu - V.Dot(p, u) * uv) / denominator;
        R margin = new(1, 8);
        Assert.True(alpha > margin && beta > margin && R.One - alpha - beta > margin,
            "The projection must be strictly inside the finite triangle, with a rational margin.");
    }

    static Face Project(V center, V anchor, V outward)
    {
        R squared = outward.LengthSquared;
        Assert.True(squared > R.Zero);
        R side = V.Dot(center - anchor, outward);
        Assert.NotEqual(R.Zero, side);
        V witness = center - outward * (side / squared);
        return new(center, witness, outward, (center - witness).LengthSquared, side > R.Zero);
    }

    internal static void AssertContactBand(Face face)
    {
        R radius = R.From(0.25f), band = R.From(0.001f);
        Assert.True(face.DistanceSquared > (radius - band).Square());
        Assert.True(face.DistanceSquared < (radius + band).Square());
        I root = face.Distance;
        Assert.True(root.Lower.Square() <= face.DistanceSquared && root.Upper.Square() >= face.DistanceSquared);
    }

    internal static void AssertPublication(CapsuleFeatureResult result, CapsuleIncidentFace incident, params Face[] expected)
    {
        R position = Error(result.PositionErrorMetres, PositionCeiling);
        R normal = Error(result.NormalError, NormalCeiling);
        R faceNormal = Error(incident.NormalError, NormalCeiling);
        R lower = R.From(result.SeparationLower), upper = R.From(result.SeparationUpper);
        Assert.True(lower <= upper && upper - lower <= WidthCeiling);
        foreach (Face face in expected)
        {
            Assert.True((V.From(result.AxisPoint) - face.Axis).LengthSquared <= position.Square());
            Assert.True((V.From(result.GeometryPoint) - face.Geometry).LengthSquared <= position.Square());
            Assert.True(lower <= face.Separation.Lower && upper >= face.Separation.Upper,
                "Published separation must contain the independently squared-valid root enclosure.");
            V direction = face.Front ? face.Outward : -face.Outward;
            AssertUnitVector(result.SeparationNormal, direction, normal);
            AssertUnitVector(incident.Normal, face.Outward, faceNormal);
        }
    }

    static R Error(float value, R ceiling)
    {
        R exact = R.From(value);
        Assert.True(exact >= R.Zero && exact <= ceiling);
        return exact;
    }

    static void AssertUnitVector(Vector3 actual, V direction, R error)
    {
        I length = Root(direction.LengthSquared);
        Assert.True(length.Lower > R.Zero);
        I x = I.Divide(direction.X, length), y = I.Divide(direction.Y, length), z = I.Divide(direction.Z, length);
        R dx = x.Farthest(R.From(actual.X)), dy = y.Farthest(R.From(actual.Y)), dz = z.Farthest(R.From(actual.Z));
        Assert.True(dx.Square() + dy.Square() + dz.Square() <= error.Square(),
            "The reported Euclidean unit-normal error must enclose independent rational/root bounds.");
    }

    internal static R Abs(R value) => value < R.Zero ? -value : value;

    internal static I Root(R value)
    {
        Assert.True(value >= R.Zero);
        const int fractionalBits = 112;
        BigInteger scale = BigInteger.One << fractionalBits;
        BigInteger integer = (value.Numerator << (2 * fractionalBits)) / value.Denominator;
        BigInteger low = BigInteger.Zero, high = BigInteger.One << ((checked((int)integer.GetBitLength()) + 2) / 2);
        while (high - low > BigInteger.One)
        {
            BigInteger middle = (low + high) >> 1;
            if (middle * middle <= integer) low = middle;
            else high = middle;
        }
        R lower = new(low, scale);
        R upper = lower.Square() == value ? lower : new R(low + 1, scale);
        Assert.True(lower.Square() <= value && upper.Square() >= value);
        return new(lower, upper);
    }

    // Exact round-to-nearest-even binary32. No host multiply/add supplies an expected coefficient.
    internal static R Single(R value)
    {
        if (value == R.Zero) return R.Zero;
        bool negative = value < R.Zero;
        R positive = Abs(value);
        int exponent = checked((int)positive.Numerator.GetBitLength() - (int)positive.Denominator.GetBitLength());
        bool BelowPower(int e) => e >= 0 ? positive.Numerator < (positive.Denominator << e)
            : (positive.Numerator << -e) < positive.Denominator;
        if (BelowPower(exponent)) exponent--;
        Assert.InRange(exponent, -149, 127);
        int quantum = Math.Max(exponent - 23, -149);
        BigInteger numerator = quantum < 0 ? positive.Numerator << -quantum : positive.Numerator;
        BigInteger denominator = quantum > 0 ? positive.Denominator << quantum : positive.Denominator;
        BigInteger quotient = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
        int halfway = (2 * remainder).CompareTo(denominator);
        if (halfway > 0 || (halfway == 0 && !quotient.IsEven)) quotient++;
        R rounded = quantum < 0 ? new(quotient, BigInteger.One << -quantum) : new(quotient << quantum, 1);
        return negative ? -rounded : rounded;
    }

    internal readonly record struct I(R Lower, R Upper)
    {
        internal static I Divide(R numerator, I denominator)
        {
            Assert.True(denominator.Lower > R.Zero);
            R a = numerator / denominator.Lower, b = numerator / denominator.Upper;
            return a <= b ? new(a, b) : new(b, a);
        }
        internal R Farthest(R value) => Max(Abs(value - Lower), Abs(value - Upper));
        public static I operator -(I a, R b) => new(a.Lower - b, a.Upper - b);
        static R Max(R a, R b) => a > b ? a : b;
    }

    internal readonly record struct M(V X, V Y, V Z)
    {
        internal V Apply(V v) => X * v.X + Y * v.Y + Z * v.Z;
        internal R Determinant => V.Dot(X, V.Cross(Y, Z));
        internal M Transpose => new(new(X.X, Y.X, Z.X), new(X.Y, Y.Y, Z.Y), new(X.Z, Y.Z, Z.Z));
        internal V Inverse(V v)
        {
            R d = Determinant;
            Assert.NotEqual(R.Zero, d);
            return new(V.Dot(v, V.Cross(Y, Z)) / d, V.Dot(v, V.Cross(Z, X)) / d,
                V.Dot(v, V.Cross(X, Y)) / d);
        }
        // Geometry described by an exact transpose-as-inverse local ray test.
        internal V TransposeInverseImage(V local) => Transpose.Inverse(local);
    }

    internal readonly record struct V(R X, R Y, R Z)
    {
        internal static V Zero => new(R.Zero, R.Zero, R.Zero);
        internal static V UnitX => new(R.One, R.Zero, R.Zero);
        internal static V UnitY => new(R.Zero, R.One, R.Zero);
        internal static V UnitZ => new(R.Zero, R.Zero, R.One);
        internal static V From(Vector3 v) => new(R.From(v.X), R.From(v.Y), R.From(v.Z));
        internal Vector3 AsSingle() => new(X.AsSingle(), Y.AsSingle(), Z.AsSingle());
        internal R LengthSquared => Dot(this, this);
        internal static R Dot(V a, V b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        internal static V Cross(V a, V b) => new(a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public static V operator +(V a, V b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V operator -(V a, V b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V operator -(V a) => Zero - a;
        public static V operator *(V a, R b) => new(a.X * b, a.Y * b, a.Z * b);
        public static V operator /(V a, R b) => new(a.X / b, a.Y / b, a.Z / b);
    }

    internal readonly record struct R
    {
        internal BigInteger Numerator { get; }
        internal BigInteger Denominator { get; }
        internal static R Zero => new(0, 1);
        internal static R One => new(1, 1);
        internal R(BigInteger numerator, BigInteger denominator)
        {
            if (denominator.IsZero) throw new ArgumentOutOfRangeException(nameof(denominator));
            if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
            BigInteger gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
            Numerator = numerator / gcd;
            Denominator = denominator / gcd;
        }
        internal R Square() => this * this;
        internal float AsSingle()
        {
            float proposal = (float)((double)Numerator / (double)Denominator);
            Assert.Equal(this, From(proposal));
            return proposal;
        }
        internal static R From(double value)
        {
            Assert.True(double.IsFinite(value));
            ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
            int encoded = (int)((bits >> 52) & 0x7ff);
            BigInteger significand = bits & 0x000f_ffff_ffff_ffffUL;
            if (encoded != 0) significand += BigInteger.One << 52;
            int exponent = encoded == 0 ? -1074 : encoded - 1075;
            if ((bits >> 63) != 0) significand = -significand;
            return exponent < 0 ? new(significand, BigInteger.One << -exponent) : new(significand << exponent, 1);
        }
        public static R operator +(R a, R b) => new(a.Numerator * b.Denominator + b.Numerator * a.Denominator,
            a.Denominator * b.Denominator);
        public static R operator -(R a, R b) => a + -b;
        public static R operator -(R a) => new(-a.Numerator, a.Denominator);
        public static R operator *(R a, R b) => new(a.Numerator * b.Numerator, a.Denominator * b.Denominator);
        public static R operator /(R a, R b) => new(a.Numerator * b.Denominator, a.Denominator * b.Numerator);
        public static bool operator <(R a, R b) => a.Numerator * b.Denominator < b.Numerator * a.Denominator;
        public static bool operator >(R a, R b) => b < a;
        public static bool operator <=(R a, R b) => !(a > b);
        public static bool operator >=(R a, R b) => !(a < b);
        public override string ToString() => $"{Numerator}/{Denominator}";
    }
}
