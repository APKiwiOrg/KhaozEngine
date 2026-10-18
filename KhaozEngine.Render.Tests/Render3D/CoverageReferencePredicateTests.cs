using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageReferencePredicateTests
{
    [Fact]
    public void Exact_sign_and_rounded_value_match_an_independent_integer_oracle_for_binary32_inputs()
    {
        var random = new Random(2704);
        // Bounded finite binary32 coordinates, including subnormals and widely separated exponents.
        for (int i = 0; i < 1024; i++)
        {
            Vector3 a = Point(), b = Point(), c = Point(), d = Point();
            CheckEdge(a, b, c);
            CheckDot(a, b, c, d);
        }
        Vector3 Point() => new(Coordinate(), Coordinate(), 0);
        float Coordinate() => BitConverter.Int32BitsToSingle(random.Next(0, 137) << 23 | random.Next(0, 1 << 23)
            | (random.Next(2) == 0 ? 0 : int.MinValue));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, -1)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(3, -1)]
    public void Binary32_midpoints_and_both_double_rounding_directions_round_to_even(int halfUlps, int tail)
    {
        // det = 1 + halfUlps*2^-24 - tail*2^-80. The tail is below a binary64 ULP at the midpoint.
        Vector3 a = new(tail * MathF.ScaleB(1, -80), -halfUlps * MathF.ScaleB(1, -24), 0);
        Vector3 b = new(1, 0, 0), p = new(0, 1, 0);
        BigInteger exact = Edge(a, b, p);
        BigInteger midpoint = (BigInteger.One << 298) + (halfUlps * (BigInteger.One << 274));
        Assert.Equal(midpoint - (tail * (BigInteger.One << 218)), exact);
        float expected = halfUlps == 1
            ? tail < 0 ? MathF.BitIncrement(1) : 1
            : tail > 0 ? MathF.BitIncrement(1) : MathF.BitIncrement(MathF.BitIncrement(1));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(Round(exact)));
        if (halfUlps == 1 && tail < 0 || halfUlps == 3 && tail > 0)
            Assert.NotEqual(expected, (float)(1d + (halfUlps * Math.ScaleB(1, -24)) - (tail * Math.ScaleB(1, -80))));
        CheckEdge(a, b, p);
        CheckEdge(a, p, b);
        CheckDot(a, b, new(a.Y, -a.X, 0), new(p.Y, -p.X, 0));
    }

    [Fact]
    public void Near_collinear_review_inputs_and_collinear_and_underflow_cases_match_the_integer_oracle()
    {
        Vector3 a = new(-0.050000011920928955f, -0.04999999701976776f, 0);
        Vector3[] points = [a, new(0.10000000149011612f, 0.10000000894069672f, 0),
            new(0.20000001788139343f, 0.20000000298023224f, 0), new(-.2f, .2f, 0), Vector3.Zero];
        Vector3[] sheet = [new(.25f, -.25f, .04f), new(.25f, .25f, .04f), new(0, 0, .02f)];
        foreach (Vector3 p in points)
            for (int i = 0; i < 3; i++) CheckEdge(sheet[i], sheet[(i + 1) % 3], p);
        CheckEdge(Vector3.Zero, new(.25f, .25f, 0), new(-.05f, -.05f, 0));
        CheckEdge(Vector3.Zero, new(float.Epsilon, 0, 0), new(0, .25f, 0));
        CheckEdge(Vector3.Zero, new(float.Epsilon, 0, 0), new(0, -.25f, 0));
        CheckEdge(new(float.MaxValue, float.MaxValue, 0), new(-float.MaxValue, 0, 0), new(0, -float.MaxValue, 0));
        CheckDot(a, points[1], sheet[1], sheet[2]);
        CheckDot(new(float.Epsilon, 0, 0), Vector3.Zero, Vector3.Zero, new(.25f, 0, 0));
    }

    [Fact]
    public void Both_frozen_group_224_incident_faces_match_the_integer_oracle()
    {
        var c = CoverageReferenceCases.Get("cost-limit-l2-lifted-w1");
        var capture = CoverageExactCaptureTests.Read(c.Id);
        var scratch = new CoverageReferenceScratch(c.Topology);
        Assert.True(CoverageReferenceFrames.TryPrepare(c.Topology, c.Model, c.Commands, scratch, out _));
        Assert.True(CoverageReferenceConstructor.TryInterpolate(c.Topology, c.Posed, scratch, out _));
        double[] target = new double[capture.ClearanceCoverage.Length];
        for (int i = 0; i < target.Length; i++) target[i] = capture.ClearanceCoverage[i] + capture.SlackCoverage[i];
        Assert.True(CoverageReferenceConstructor.TryConformGroups(c.Topology, c.Commands, scratch, out _, target));
        var frame = scratch.Frames[scratch.RegionCommands[1]];
        var support = c.Commands[frame.Command].Support.ReferenceData;
        var t = support.Triangles[1];
        Vector3[] sheet = [support.Positions[t.A], support.Positions[t.B], support.Positions[t.C]];
        foreach (int face in new[] { 459, 463 })
        {
            Vector3[] host = new Vector3[3];
            for (int i = 0; i < 3; i++) host[i] = CoverageReferenceMath.Point(new CoverageReferenceArithmetic(),
                scratch.After[(int)c.Topology.Indices[face * 3 + i]].Position, frame.HostToCoverage);
            CheckEdge(host[0], host[1], host[2]);
            CheckEdge(sheet[0], sheet[1], sheet[2]);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    CheckEdge(sheet[j], sheet[(j + 1) % 3], host[i]);
                    CheckEdge(host[i], host[(i + 1) % 3], sheet[j]);
                }
        }
    }

    static void CheckEdge(Vector3 a, Vector3 b, Vector3 p)
    {
        var actual = CoverageReferencePredicates.Edge(a, b, p);
        BigInteger exact = Edge(a, b, p);
        Assert.Equal(exact.Sign, actual.Sign);
        Assert.Equal(BitConverter.SingleToInt32Bits(Round(exact)), BitConverter.SingleToInt32Bits(actual.Value));
    }

    static void CheckDot(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        var actual = CoverageReferencePredicates.Dot(a, b, c, d);
        BigInteger exact = ((Integer(b.X) - Integer(a.X)) * (Integer(d.X) - Integer(c.X)))
            + ((Integer(b.Y) - Integer(a.Y)) * (Integer(d.Y) - Integer(c.Y)));
        Assert.Equal(exact.Sign, actual.Sign);
        Assert.Equal(BitConverter.SingleToInt32Bits(Round(exact)), BitConverter.SingleToInt32Bits(actual.Value));
    }

    // Integer units of 2^-149. Products and determinants therefore have units of 2^-298.
    static BigInteger Integer(float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        int exponent = (int)((bits >> 23) & 255);
        BigInteger significand = bits & 0x7fffff;
        if (exponent != 0) significand = (significand + (1 << 23)) << (exponent - 1);
        return (bits & 0x80000000) == 0 ? significand : -significand;
    }

    static BigInteger Edge(Vector3 a, Vector3 b, Vector3 p) =>
        ((Integer(b.X) - Integer(a.X)) * (Integer(p.Y) - Integer(a.Y)))
        - ((Integer(b.Y) - Integer(a.Y)) * (Integer(p.X) - Integer(a.X)));

    static float Round(BigInteger exact)
    {
        BigInteger magnitude = BigInteger.Abs(exact);
        int shift = Math.Max(149, (int)magnitude.GetBitLength() - 24);
        BigInteger significand = magnitude >> shift;
        BigInteger remainder = magnitude - (significand << shift), half = BigInteger.One << (shift - 1);
        if (remainder > half || remainder == half && !significand.IsEven) significand++;
        float rounded = MathF.ScaleB((float)(int)significand, shift - 298);
        return exact.Sign < 0 ? -rounded : rounded;
    }
}
