using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class WaterColumnOpticsTests
{
    static readonly Color[] RequestedColors =
    [
        new(0f, 0f, 0f),
        new(0.04f, 0.13f, 0.30f),
        new(0.10f, 0.13f, 0.10f),
        new(1f, 1f, 1f),
        new(1f, 0.13f, 0f),
    ];

    [Theory]
    [InlineData(0.1f)]
    [InlineData(2f)]
    [InlineData(100f)]
    public void ReachableColorRoundTrips(float clarity)
    {
        foreach (Color color in RequestedColors)
        {
            var c = WaterColumnOptics.Derive(color, clarity);
            var expected = new Vector3(color.R, color.G, color.B);
            Assert.InRange(Vector3.Distance(WaterColumnOptics.ReferenceDeepRadiance(c.Albedo), expected),
                0f, 0.00002f);
            Assert.InRange(c.Albedo.X, 0f, 1f);
            Assert.InRange(c.Albedo.Y, 0f, 1f);
            Assert.InRange(c.Albedo.Z, 0f, 1f);
        }
    }

    [Fact]
    public void WhiteIsReachableBelowUnitAlbedo()
    {
        var white = WaterColumnOptics.Derive(Color.White, 2f);
        Assert.InRange(white.Albedo.X, 0.94f, 0.955f);
        Assert.Equal(white.Albedo.X, white.Albedo.Y);
        Assert.Equal(white.Albedo.X, white.Albedo.Z);
        Assert.InRange(Vector3.Distance(WaterColumnOptics.ReferenceDeepRadiance(white.Albedo), Vector3.One),
            0f, 0.00002f);
        Assert.Equal(2.25f, WaterColumnOptics.ReferenceDeepRadiance(Vector3.One).X, 5);
        Assert.Equal(WaterColumnOptics.MaximumReferenceColor,
            WaterColumnOptics.ReferenceDeepRadiance(Vector3.One).X);
    }

    [Fact]
    public void ExtinctionHasTheAuthoredMeanAndConservesEnergy()
    {
        var c = WaterColumnOptics.Derive(new Color(0.04f, 0.13f, 0.30f), 2f);
        Assert.InRange(Vector3.Distance(WaterColumnOptics.ReferenceDeepRadiance(c.Albedo),
            new Vector3(0.04f, 0.13f, 0.30f)), 0f, 0.00002f);
        Assert.InRange(MathF.Abs((c.Extinction.X + c.Extinction.Y + c.Extinction.Z) / 3f - 1.5f),
            0f, 0.000001f);
        Assert.InRange(MathF.Exp(-(c.Extinction.X + c.Extinction.Y + c.Extinction.Z) / 3f * 2f),
            0.04978f, 0.04980f);
        Assert.True(c.Extinction.X > c.Extinction.Y && c.Extinction.Y > c.Extinction.Z);
        Assert.InRange(Vector3.Distance(c.Extinction, new Vector3(1.5875f, 1.52f, 1.3925f)),
            0f, 0.000001f);
        Assert.InRange(Vector3.Distance(c.Absorption + c.Scattering, c.Extinction), 0f, 0.000001f);

        foreach (Color color in RequestedColors)
        foreach (float clarity in new[] { 0.1f, 2f, 100f })
        {
            var coefficients = WaterColumnOptics.Derive(color, clarity);
            float mean = (coefficients.Extinction.X + coefficients.Extinction.Y + coefficients.Extinction.Z) / 3f;
            Assert.InRange(MathF.Abs(mean * clarity - 3f), 0f, 0.000001f);
            Assert.InRange(Vector3.Distance(coefficients.Absorption + coefficients.Scattering,
                coefficients.Extinction) / mean, 0f, 0.000001f);
            Assert.InRange(Vector3.Distance(coefficients.Scattering,
                coefficients.Albedo * coefficients.Extinction) / mean, 0f, 0.000001f);
            AssertFiniteNonNegative(coefficients.Absorption);
        }
    }

    [Theory]
    [InlineData(0.001f)]
    [InlineData(0.1f)]
    [InlineData(2f)]
    [InlineData(100f)]
    [InlineData(float.MaxValue)]
    public void BlackWhiteAndExtremeClarityStayFinite(float clarity)
    {
        foreach (Color color in RequestedColors)
        {
            var c = WaterColumnOptics.Derive(color, clarity);
            AssertFiniteNonNegative(c.Extinction);
            AssertFiniteNonNegative(c.Scattering);
            AssertFiniteNonNegative(c.Absorption);
            AssertFiniteNonNegative(c.Albedo);
            Assert.True(c.Extinction.X > 0f && c.Extinction.Y > 0f && c.Extinction.Z > 0f);
        }

        var black = WaterColumnOptics.Derive(new Color(0f, 0f, 0f), clarity);
        Assert.Equal(Vector3.Zero, black.Albedo);
        Assert.Equal(Vector3.Zero, black.Scattering);
        Assert.Equal(black.Extinction, black.Absorption);
        Assert.Equal(Vector3.Zero, WaterColumnOptics.ReferenceDeepRadiance(black.Albedo));
        var mixed = WaterColumnOptics.Derive(new Color(1f, 0.13f, 0f), clarity);
        Assert.Equal(0f, mixed.Albedo.Z);
        Assert.Equal(0f, mixed.Scattering.Z);
    }

    [Theory]
    [InlineData(0f, 1f, 1f)]
    [InlineData(1f, 1f, 3f)]
    [InlineData(0.75f, 1f, 1.5f)]
    [InlineData(0.75f, 0.5f, 1.3333334f)]
    [InlineData(1f, 0f, 1f)]
    public void HMatchesIsotropicApproximation(float albedo, float mu, float expected)
        => Assert.Equal(expected, WaterColumnOptics.H(albedo, mu), 6);

    static void AssertFiniteNonNegative(Vector3 value)
    {
        Assert.InRange(value.X, 0f, float.MaxValue);
        Assert.InRange(value.Y, 0f, float.MaxValue);
        Assert.InRange(value.Z, 0f, float.MaxValue);
    }
}
