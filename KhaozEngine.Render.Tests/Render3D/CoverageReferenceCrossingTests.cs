using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageReferenceCrossingTests
{
    [Theory]
    [InlineData(-74)]
    [InlineData(-75)]
    public void Subnormal_opposite_determinants_use_the_scaled_rational_parameter(int exponent)
    {
        // Exact sides are unit squared and minus twice that value. The independent rational quotients are 1/3
        // and 2/3. Exponent -74 gives representable subnormals, -75 also tests underflow before normalization.
        float unit = MathF.ScaleB(1, exponent);
        Vector3 s = Vector3.Zero, t = new(unit, 0, 0), a = new(0, unit, 0), b = new(unit, -2 * unit, 0);
        Assert.Equal(BitConverter.Int32BitsToSingle(0x3eaaaaab), new CoverageReferenceArithmetic().CrossingParameter(s, t, a, b));
        Assert.Equal(BitConverter.Int32BitsToSingle(0x3f2aaaab), new CoverageReferenceArithmetic().CrossingParameter(s, t, b, a));
    }

    [Theory]
    [InlineData(1f, -2f)]
    [InlineData(-3f, 5f)]
    [InlineData(.75f, -1.25f)]
    [InlineData(1.9999999f, -2.0000002f)]
    public void Crossing_parameter_equals_the_unscaled_quotient_for_normal_sides(float first, float second)
    {
        Vector3 s = Vector3.Zero, t = Vector3.UnitX, a = new(0, first, 0), b = new(0, second, 0);
        var ca = CoverageReferencePredicates.Edge(s, t, a);
        var cb = CoverageReferencePredicates.Edge(s, t, b);
        Assert.True(float.IsNormal(ca.Value) && float.IsNormal(cb.Value));
        Assert.True(ca.Sign * cb.Sign < 0);
        float expected = ca.Value / (ca.Value - cb.Value);
        Assert.Equal(BitConverter.SingleToInt32Bits(expected),
            BitConverter.SingleToInt32Bits(new CoverageReferenceArithmetic().CrossingParameter(s, t, a, b)));
    }

    [Fact]
    public void Crossing_parameter_rejects_zero_and_same_sign_sides()
    {
        foreach (var (first, second) in new[] { (0f, 1f), (1f, 0f), (1f, 2f), (-1f, -2f) })
            Assert.True(float.IsNaN(new CoverageReferenceArithmetic().CrossingParameter(Vector3.Zero, Vector3.UnitX,
                new(0, first, 0), new(0, second, 0))));
        Assert.True(float.IsNaN(new CoverageReferenceArithmetic().CrossingParameter(Vector3.Zero, Vector3.Zero,
            Vector3.UnitY, -Vector3.UnitY)));
        foreach (float nonfinite in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            for (int input = 0; input < 4; input++)
                foreach (Vector3 point in new[] { new Vector3(nonfinite, 0, 0), new Vector3(0, nonfinite, 0) })
                {
                    Vector3[] points = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY];
                    points[input] = point;
                    Assert.True(float.IsNaN(new CoverageReferenceArithmetic().CrossingParameter(points[0], points[1], points[2], points[3])));
                }
    }
}
