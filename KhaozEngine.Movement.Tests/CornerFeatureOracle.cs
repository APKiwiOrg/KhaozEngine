using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using BepuMesh = BepuPhysics.Collidables.Mesh;
using BepuSim = BepuPhysics.Simulation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

// Test-only rational geometry. No production arithmetic, classification, sweep point or ray witness is an oracle.
internal static class CornerFeatureOracle
{
    internal static readonly R PositionCeiling = new(1, 4000);
    internal static readonly R NearContact = new(1, 10000);
    internal static readonly R NormalCeiling = new(1, 100000);
    internal static ExactVector V(int x, int y, int z, int denominator = 1) =>
        new(new R(x, denominator), new R(y, denominator), new R(z, denominator));
    internal static ExactVector Point(Vector3 p) => new(R.From(p.X), R.From(p.Y), R.From(p.Z));
    internal static ExactVector AxisLower(CapsuleShape capsule, Pose pose) =>
        Point(pose.Position) - new ExactVector(R.Zero, R.From(capsule.Length) / new R(2, 1), R.Zero);
    internal static ExactVector AxisUpper(CapsuleShape capsule, Pose pose) =>
        Point(pose.Position) + new ExactVector(R.Zero, R.From(capsule.Length) / new R(2, 1), R.Zero);

    internal static R Error(float value, R ceiling)
    {
        R error = R.From(value);
        Assert.True(error >= R.Zero && error <= ceiling);
        return error;
    }

    internal static void AssertVectorWithin(Vector3 actual, ExactVector expected, R error, string field) =>
        Assert.True((Point(actual) - expected).LengthSquared() <= error * error,
            $"The reported {field} error excludes the exact independent witness.");

    internal static void AssertNearContactSquared(R squared, R radius)
    {
        R low = radius - NearContact, high = radius + NearContact;
        Assert.True(low > R.Zero && low * low <= squared && squared <= high * high,
            "The unchanged represented candidate must already be near contact with the actual finite geometry.");
    }

    internal static void AssertComplete(IPhysicsWorld view, BepuPhysicsWorld world, IPhysicsCapsuleFeatures features,
        IPhysicsQueryLease lease, StaticHandle target, CapsuleShape capsule, CapsuleFeatureResult result,
        CapsuleIncidentFace[] faces, CapsuleIncidentFace[] original, CapsuleFeatureKind kind,
        ExactVector axis, ExactVector geometry, ExactVector[] normals)
    {
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        Assert.Same(view, result.QueryWorld);
        Assert.Same(world, result.SourceWorld);
        Assert.Same(lease, result.Lease);
        Assert.Equal(lease.Origin, result.Origin);
        Assert.Equal(lease.GeometryGeneration, result.GeometryGeneration);
        Assert.Equal(target, result.Target);
        Assert.True(result.LeafId >= 0 && result.FeatureId >= 0);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(normals.Length, result.Written);
        Assert.Equal(0, result.RequiredCapacity);
        features.AssertFeatureCurrent(result, lease);
        Assert.Throws<InvalidOperationException>(() => world.AssertFeatureCurrent(result, lease));
        R error = Error(result.PositionErrorMetres, PositionCeiling);
        AssertVectorWithin(result.AxisPoint, axis, error, "axis");
        AssertVectorWithin(result.GeometryPoint, geometry, error, "geometry");
        ExactVector delta = axis - geometry;
        R squared = delta.LengthSquared(), radius = R.From(capsule.Radius);
        Assert.True(squared > R.Zero);
        AssertNormalizedWithin(result.SeparationNormal, delta, Error(result.NormalError, NormalCeiling));
        R low = R.From(result.SeparationLower), high = R.From(result.SeparationUpper);
        Assert.True(low <= high && high - low <= NearContact);
        Assert.True(low >= R.Zero - NearContact && high <= NearContact,
            "The whole interval must corroborate contact, without a skin offset or snap.");
        Assert.True(radius + low >= R.Zero);
        Assert.True((radius + low) * (radius + low) <= squared);
        Assert.True((radius + high) * (radius + high) >= squared);
        var seen = new HashSet<int>();
        var matched = new bool[normals.Length];
        for (int i = 0; i < result.Written; i++)
        {
            CapsuleIncidentFace face = faces[i];
            Assert.True(face.FaceId >= 0 && seen.Add(face.FaceId));
            Assert.Equal(kind, face.Incidence);
            R faceError = Error(face.NormalError, NormalCeiling);
            int match = -1;
            for (int j = 0; j < normals.Length; j++)
                if (!matched[j] && (Point(face.Normal) - normals[j]).LengthSquared() <= faceError * faceError)
                {
                    match = j;
                    break;
                }
            Assert.True(match >= 0, "Every incident oriented normal must match the independent finite neighborhood.");
            matched[match] = true;
        }
        Assert.All(matched, value => Assert.True(value));
        for (int i = result.Written; i < faces.Length; i++) Assert.Equal(original[i], faces[i]);
    }

    internal static void AssertNormalizedWithin(Vector3 actual, ExactVector delta, R error)
    {
        (R lo, R hi) = Root(delta.LengthSquared());
        Assert.True(lo > R.Zero);
        R ex = ComponentError(R.From(actual.X), delta.X, lo, hi);
        R ey = ComponentError(R.From(actual.Y), delta.Y, lo, hi);
        R ez = ComponentError(R.From(actual.Z), delta.Z, lo, hi);
        Assert.True(ex * ex + ey * ey + ez * ez <= error * error,
            "The reported Euclidean normal error excludes the exact normalized separation direction.");
    }

    static R ComponentError(R proposed, R component, R lo, R hi)
    {
        R a = Abs(proposed - component / lo), b = Abs(proposed - component / hi);
        return a >= b ? a : b;
    }

    static R Abs(R value) => value < R.Zero ? R.Zero - value : value;

    // Integer bisection encloses the exact root on a 2^-120 grid. Every endpoint is checked by squaring.
    // This is bounded arithmetic within one fixed fixture, rather than a randomized or load loop.
    static (R Lower, R Upper) Root(R value)
    {
        Assert.True(value > R.Zero && value <= new R(4096, 1));
        BigInteger scale = BigInteger.One << 120;
        BigInteger quotient = value.Numerator * scale * scale / value.Denominator;
        BigInteger lower = 0, upper = BigInteger.One << 127;
        for (int step = 0; step < 127 && upper - lower > 1; step++)
        {
            BigInteger middle = (lower + upper) >> 1;
            if (middle * middle <= quotient) lower = middle;
            else upper = middle;
        }
        Assert.Equal(BigInteger.One, upper - lower);
        R lo = new(lower, scale), hi = new(upper, scale);
        Assert.True(lo * lo <= value && hi * hi >= value);
        if (lo * lo == value) hi = lo;
        return (lo, hi);
    }

    internal static CapsuleIncidentFace[] Sentinels(int count = 8)
    {
        var faces = new CapsuleIncidentFace[count];
        for (int i = 0; i < count; i++) faces[i] = new(900 + i, new Vector3(7, -11, 13), 0.125f, CapsuleFeatureKind.Vertex);
        return faces;
    }

    internal static void AssertRefused(CapsuleFeatureResult result, CapsuleIncidentFace[] faces,
        CapsuleIncidentFace[] original, CapsuleFeatureStatus status, int required = 0)
    {
        Assert.Equal(status, result.Status);
        Assert.Equal(0, result.Written);
        Assert.Equal(required, result.RequiredCapacity);
        Assert.Equal(original, faces);
        Assert.Null(result.QueryWorld);
        Assert.Null(result.SourceWorld);
        Assert.Null(result.Lease);
        Assert.Equal(default(StaticHandle), result.Target);
        Assert.Equal(CapsuleFeatureKind.None, result.Kind);
        Assert.Equal(Vector3.Zero, result.AxisPoint);
        Assert.Equal(Vector3.Zero, result.GeometryPoint);
        Assert.Equal(Vector3.Zero, result.SeparationNormal);
        Assert.Equal(0d, result.SeparationLower);
        Assert.Equal(0d, result.SeparationUpper);
        Assert.Equal(0f, result.PositionErrorMetres);
        Assert.Equal(0f, result.NormalError);
    }

    internal static void AssertInstalledMesh(BepuPhysicsWorld world, Pose pose, Triangle[] expected)
    {
        FieldInfo? field = typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        BepuSim simulation = Assert.IsType<BepuSim>(field.GetValue(world));
        int found = -1;
        for (int i = 0; i < simulation.Statics.Count; i++)
            if (simulation.Statics[i].Shape.Type == default(BepuMesh).TypeId)
            {
                Assert.Equal(-1, found);
                found = i;
            }
        Assert.True(found >= 0);
        ref var body = ref simulation.Statics[found];
        Assert.Equal(pose.Position, body.Pose.Position);
        Assert.Equal(pose.Orientation, body.Pose.Orientation);
        ref BepuMesh mesh = ref simulation.Shapes.GetShape<BepuMesh>(body.Shape.Index);
        Assert.Equal(Vector3.One, mesh.Scale);
        Assert.Equal(expected.Length, mesh.Triangles.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            ref var triangle = ref mesh.Triangles[i];
            Assert.Equal(expected[i].A, triangle.A);
            Assert.Equal(expected[i].B, triangle.B);
            Assert.Equal(expected[i].C, triangle.C);
        }
    }

    internal readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C);
    internal readonly record struct ExactVector(R X, R Y, R Z)
    {
        internal R LengthSquared() => X * X + Y * Y + Z * Z;
        public static ExactVector operator +(ExactVector a, ExactVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static ExactVector operator -(ExactVector a, ExactVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    }

    internal readonly record struct R
    {
        internal BigInteger Numerator { get; }
        internal BigInteger Denominator { get; }
        internal static R Zero => new(0, 1);
        internal R(BigInteger numerator, BigInteger denominator)
        {
            Assert.False(denominator.IsZero);
            if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
            BigInteger divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
            Numerator = numerator / divisor;
            Denominator = denominator / divisor;
        }

        internal static R From(double value)
        {
            Assert.True(double.IsFinite(value));
            ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
            int encoded = (int)((bits >> 52) & 0x7ff);
            BigInteger mantissa = bits & 0x000f_ffff_ffff_ffffUL;
            if (encoded != 0) mantissa += BigInteger.One << 52;
            if ((bits >> 63) != 0) mantissa = -mantissa;
            int exponent = encoded == 0 ? -1074 : encoded - 1023 - 52;
            return exponent >= 0 ? new(mantissa << exponent, 1) : new(mantissa, BigInteger.One << -exponent);
        }

        public static R operator +(R a, R b) => new(a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator);
        public static R operator -(R a, R b) => new(a.Numerator * b.Denominator - b.Numerator * a.Denominator, a.Denominator * b.Denominator);
        public static R operator *(R a, R b) => new(a.Numerator * b.Numerator, a.Denominator * b.Denominator);
        public static R operator /(R a, R b) => new(a.Numerator * b.Denominator, a.Denominator * b.Numerator);
        public static bool operator <=(R a, R b) => a.Numerator * b.Denominator <= b.Numerator * a.Denominator;
        public static bool operator >=(R a, R b) => a.Numerator * b.Denominator >= b.Numerator * a.Denominator;
        public static bool operator <(R a, R b) => a.Numerator * b.Denominator < b.Numerator * a.Denominator;
        public static bool operator >(R a, R b) => a.Numerator * b.Denominator > b.Numerator * a.Denominator;
        public override string ToString() => $"{Numerator}/{Denominator}";
    }
}
