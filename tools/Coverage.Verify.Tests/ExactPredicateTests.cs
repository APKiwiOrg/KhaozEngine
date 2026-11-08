using System;
using System.Numerics;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class ExactPredicateTests
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
        BigInteger exact = Binary32IntegerOracle.Edge(a, b, p);
        BigInteger midpoint = (BigInteger.One << 298) + (halfUlps * (BigInteger.One << 274));
        Assert.Equal(midpoint - (tail * (BigInteger.One << 218)), exact);
        float expected = halfUlps == 1
            ? tail < 0 ? MathF.BitIncrement(1) : 1
            : tail > 0 ? MathF.BitIncrement(1) : MathF.BitIncrement(MathF.BitIncrement(1));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(Binary32IntegerOracle.Round(exact)));
        if (halfUlps == 1 && tail < 0 || halfUlps == 3 && tail > 0)
            Assert.NotEqual(expected, (float)(1d + (halfUlps * Math.ScaleB(1, -24)) - (tail * Math.ScaleB(1, -80))));
        CheckEdge(a, b, p);
        CheckEdge(a, p, b);
        CheckDot(a, b, new(a.Y, -a.X, 0), new(p.Y, -p.X, 0));
    }

    static void CheckEdge(Vector3 a, Vector3 b, Vector3 p)
    {
        var actual = CoverageReferencePredicates.Edge(a, b, p);
        BigInteger exact = Binary32IntegerOracle.Edge(a, b, p);
        Assert.Equal(exact.Sign, actual.Sign);
        Assert.Equal(BitConverter.SingleToInt32Bits(Binary32IntegerOracle.Round(exact)), BitConverter.SingleToInt32Bits(actual.Value));
    }

    static void CheckDot(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        var actual = CoverageReferencePredicates.Dot(a, b, c, d);
        BigInteger exact = ((Binary32IntegerOracle.Integer(b.X) - Binary32IntegerOracle.Integer(a.X)) * (Binary32IntegerOracle.Integer(d.X) - Binary32IntegerOracle.Integer(c.X)))
            + ((Binary32IntegerOracle.Integer(b.Y) - Binary32IntegerOracle.Integer(a.Y)) * (Binary32IntegerOracle.Integer(d.Y) - Binary32IntegerOracle.Integer(c.Y)));
        Assert.Equal(exact.Sign, actual.Sign);
        Assert.Equal(BitConverter.SingleToInt32Bits(Binary32IntegerOracle.Round(exact)), BitConverter.SingleToInt32Bits(actual.Value));
    }

}
