using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

public sealed class WaterColumnCompositeTests(ITestOutputHelper output)
{
    static readonly float[] Lit = [1f, 1f, 1f, 1f];
    static readonly float[] Dark = [0f, 0f, 0f, 0f];
    static readonly float[] Alternating = [0f, 1f, 0f, 1f];
    static readonly Vector3 Bed = new(0.7f, 0.4f, 0.2f);
    static readonly WaterColumnCoefficients Coefficients = WaterColumnOptics.Derive(new Color(0.12f, 0.3f, 0.6f), 2f);

    [Fact]
    public void ZeroDistanceReturnsTheCopiedBed()
    {
        Assert.Equal(Bed, WaterColumnComposite.Transmitted(Coefficients, Bed, Vector3.One, Vector3.One,
            1f, 1f, 0f, Lit, 0.25f));
    }

    [Fact]
    public void ReferenceDeepMatchesDerivedColor()
    {
        var requestedWaterColor = new Vector3(0.12f, 0.3f, 0.6f);
        Vector3 deep = WaterColumnComposite.Transmitted(Coefficients, Bed, Vector3.One, Vector3.One,
            1f, 1f, 16f, Lit, 0.25f);
        Assert.InRange(Vector3.Distance(deep, requestedWaterColor), 0f, 0.00002f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.05f)]
    [InlineData(0.30f)]
    [InlineData(2f)]
    [InlineData(16f)]
    public void FourStrataIntegrateConstantVisibilityExactly(float distance)
    {
        // Suppress only the H estimate so the assertion measures the single-scattering integral alone.
        WaterColumnCoefficients c = Coefficients with { Albedo = Vector3.Zero };
        foreach (float mu in new[] { 1f, 1f / 1.267583f })
            foreach (float mu0 in new[] { 1f, 1f / 1.267583f })
                foreach (float[] visibility in new[] { Lit, Dark, Alternating })
                {
                    Vector3 expected = Vector3.Zero;
                    double meanSigma = (c.Extinction.X + c.Extinction.Y + c.Extinction.Z) / 3d;
                    double kbar = meanSigma * (1d + mu / mu0);
                    double cdf = 1d - Math.Exp(-kbar * distance);
                    for (int channel = 0; channel < 3; channel++)
                    {
                        double k = c.Extinction[channel] * (1d + mu / mu0);
                        double sum = 0d;
                        for (int i = 0; i < 4; i++)
                        {
                            double start = -Math.Log(1d - i / 4d * cdf) / kbar;
                            double end = i == 3 ? distance : -Math.Log(1d - (i + 1) / 4d * cdf) / kbar;
                            sum += visibility[i] * (Math.Exp(-k * start) - Math.Exp(-k * end)) / k;
                        }
                        expected[channel] = (float)(c.Scattering[channel] * sum / 4d);
                    }
                    Vector3 unshadowedMarch = WaterColumnComposite.Transmitted(c, Vector3.Zero, Vector3.One,
                        Vector3.Zero, mu, mu0, distance, visibility, 0.25f);
                    Vector3 analyticSingleScatter = expected;
                    Assert.InRange(Vector3.Distance(unshadowedMarch, analyticSingleScatter), 0f, 0.000001f);
                }
    }

    [Fact]
    public void AmbientSingleScatterIsUnshadowed()
    {
        WaterColumnCoefficients c = Coefficients with { Albedo = Vector3.Zero };
        Vector3 lit = WaterColumnComposite.Transmitted(c, Vector3.Zero, Vector3.Zero, Vector3.One,
            1f, 1f, 2f, Lit, 0.25f);
        Vector3 dark = WaterColumnComposite.Transmitted(c, Vector3.Zero, Vector3.Zero, Vector3.One,
            1f, 1f, 2f, Dark, 0.25f);
        Assert.Equal(lit, dark);
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(Math.Abs(lit[channel] - c.Scattering[channel]
                * (1d - Math.Exp(-c.Extinction[channel] * 4d)) / (8d * c.Extinction[channel])), 0d, 1e-7d);
    }

    [Fact]
    public void MultipleScatterDimsWithMeanVisibility()
    {
        // Sky single scattering cancels between these results. Its H estimate follows mean sun visibility.
        Vector3 lit = WaterColumnComposite.Transmitted(Coefficients, Vector3.Zero, Vector3.Zero, Vector3.One,
            1f, 1f, 2f, Lit, 0.25f);
        Vector3 dark = WaterColumnComposite.Transmitted(Coefficients, Vector3.Zero, Vector3.Zero, Vector3.One,
            1f, 1f, 2f, Dark, 0.25f);
        Vector3 half = WaterColumnComposite.Transmitted(Coefficients, Vector3.Zero, Vector3.Zero, Vector3.One,
            1f, 1f, 2f, Alternating, 0.25f);
        Assert.True(lit.X > dark.X && lit.Y > dark.Y && lit.Z > dark.Z);
        Assert.InRange(Vector3.Distance(half, (lit + dark) / 2f), 0f, 1e-7f);
    }

    [Theory]
    [InlineData(-0.05f)]
    [InlineData(0.05f)]
    public void DisplacedSurfaceStrataMatchIndependentQuadrature(float offset)
    {
        WaterColumnCoefficients c = Coefficients with { Albedo = Vector3.Zero };
        const float mu = 0.8f, mu0 = 0.79f;
        foreach (float distance in new[] { 0f, 0.05f, 0.30f, 2f, 16f })
        {
            Vector3 actual = WaterColumnComposite.Transmitted(c, Vector3.Zero, Vector3.One, Vector3.One,
                mu, mu0, distance, Lit, 0.25f, offset);
            Vector3 expected = Vector3.Zero;
            // Midpoint quadrature, independent of the production piecewise primitive and CDF.
            const int steps = 20000;
            double step = distance / (double)steps;
            for (int channel = 0; channel < 3; channel++)
            {
                double sum = 0d;
                for (int i = 0; i < steps; i++)
                {
                    double s = (i + 0.5d) * step;
                    double h = Math.Max(0d, offset + mu * s);
                    sum += Math.Exp(-c.Extinction[channel] * (s + h / mu0))
                        + Math.Exp(-c.Extinction[channel] * (s + h));
                }
                expected[channel] = (float)(sum * step * c.Scattering[channel] / 4d);
            }
            Assert.InRange(Vector3.Distance(actual, expected), 0f, 0.00001f);
            for (int i = 0; i < 4; i++)
            {
                float s = WaterColumnComposite.SampleDistance(c.Extinction, mu, mu0, distance, offset, (i + 0.25f) / 4f);
                Vector3 x = new(2f, -offset - mu * s, 3f);
                Vector3 entry = WaterColumnComposite.EntryPoint(x, new Vector3(0.6f, -mu0, 0f), 0f);
                Assert.Equal(Math.Max(x.Y, 0f), entry.Y);
                Assert.InRange(s, 0f, distance);
                if (distance > 0f)
                {
                    double meanSigma = (c.Extinction.X + c.Extinction.Y + c.Extinction.Z) / 3d;
                    double mass = ScalarQuadrature(distance), prefix = ScalarQuadrature(s);
                    Assert.InRange(Math.Abs(prefix / mass - (i + 0.25d) / 4d), 0d, 1e-5d);

                    double ScalarQuadrature(double end)
                    {
                        double sum = 0d, step = end / 20000d;
                        for (int j = 0; j < 20000; j++)
                        {
                            double t = (j + 0.5d) * step;
                            sum += Math.Exp(-meanSigma * (t + Math.Max(0d, offset + mu * t) / mu0));
                        }
                        return sum * step;
                    }
                }
            }
        }
    }

    [Fact]
    public void ThinAndZeroExtinctionUseTheAnalyticLimit()
    {
        foreach (float sigma in new[] { 0f, 0.000001f })
        {
            var c = new WaterColumnCoefficients(new Vector3(sigma), Vector3.One, Vector3.Zero, Vector3.Zero);
            Vector3 actual = WaterColumnComposite.Transmitted(c, Vector3.Zero, Vector3.One, Vector3.Zero,
                1f, 1f, 0.05f, Lit, 0.25f);
            double expected = sigma == 0f ? 0.05d / 4d : (1d - Math.Exp(-2d * sigma * 0.05d)) / (8d * sigma);
            Assert.InRange(Vector3.Distance(actual, new Vector3((float)expected)), 0f, 1e-8f);
        }
    }

    [Fact]
    public void FogOnlyOwnTerms()
    {
        Vector3 bed = Bed, own = new(0.1f, 0.2f, 0.3f);
        Assert.Equal(bed, WaterColumnComposite.ComposeFogged(bed, Vector3.One, Vector3.Zero,
            0f, 0f, 1f, new Vector3(0.2f), new Vector3(0.7f)));
        Assert.Equal(bed, WaterColumnComposite.ComposeFogged(bed, new Vector3(0.1f), own,
            0.4f, 0.8f, 0f, new Vector3(0.2f), new Vector3(0.7f)));
        Vector3 expected = bed * 0.12f + own * 0.2f + new Vector3(0.7f * 0.8f * 0.88f);
        Assert.InRange(Vector3.Distance(expected, WaterColumnComposite.ComposeFogged(bed,
            new Vector3(0.5f), own, 0.4f, 0.6f, 1f, new Vector3(0.2f), new Vector3(0.7f))), 0f, 1e-7f);
    }

    [Fact]
    public void OriginAndOrthographicRayAreInvariant()
    {
        Vector3 eye = new(10f, 8f, 5f), target = Vector3.Zero, origin = new(3f, 2f, -4f);
        foreach (Matrix4x4 projection in new[] { Matrix4x4.CreateOrthographic(20f, 10f, 0.1f, 100f),
                     Matrix4x4.CreatePerspectiveFieldOfView(1f, 2f, 0.1f, 100f) })
        {
            Matrix4x4.Invert(Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY) * projection, out var inverse);
            Matrix4x4.Invert(Matrix4x4.CreateLookAt(eye - origin, target - origin, Vector3.UnitY) * projection, out var reduced);
            Assert.True(WaterColumnComposite.TryIncidentDirection(inverse, new Vector2(0.2f, 0.3f), out var before));
            Assert.True(WaterColumnComposite.TryIncidentDirection(reduced, new Vector2(0.2f, 0.3f), out var after));
            AssertInvariant(inverse, new Vector2(0.2f, 0.3f), before, reduced, new Vector2(0.2f, 0.3f), after);
            if (projection.M44 == 1f)
            {
                Assert.True(WaterColumnComposite.TryIncidentDirection(inverse, new Vector2(-0.5f, -0.4f), out var otherPixel));
                AssertInvariant(inverse, new Vector2(0.2f, 0.3f), before, inverse, new Vector2(-0.5f, -0.4f), otherPixel);
            }
        }
        Assert.False(WaterColumnComposite.TryIncidentDirection(default, Vector2.Zero, out _));
    }

    void AssertInvariant(Matrix4x4 firstInverse, Vector2 firstPixel, Vector3 first,
        Matrix4x4 secondInverse, Vector2 secondPixel, Vector3 second)
    {
        var a = IncidentDirectionRoundoff.Evaluate(firstInverse, firstPixel);
        var b = IncidentDirectionRoundoff.Evaluate(secondInverse, secondPixel);
        Assert.InRange(Vector3.Distance(first, a.Direction), 0f, a.Error);
        Assert.InRange(Vector3.Distance(second, b.Direction), 0f, b.Error);
        // In perspective, z=0 and z=0.5 are only 0.103 m apart at coordinates near 10 m.
        // Subtracting the unprojected float positions amplifies roundoff by about 100.
        // Keep the original bound on the input matrices' independent double rays, then add
        // the derived transform/divide/subtract/normalize bounds for both float evaluations.
        float inputDrift = Vector3.Distance(a.Direction, b.Direction);
        Assert.InRange(inputDrift, 0f, 0.00002f);
        float tolerance = MathF.BitIncrement(inputDrift + a.Error + b.Error);
        Assert.InRange(Vector3.Distance(first, second), 0f, tolerance);
        output.WriteLine($"Input ray drift {inputDrift:R}, float evaluation bounds {a.Error:R}, {b.Error:R}, total {tolerance:R}");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    [InlineData(90f)]
    public void MinimumClarityAndWaterlineStayFiniteAtSolarElevations(float elevation)
    {
        WaterColumnCoefficients c = WaterColumnOptics.Derive(new Color(0.3f, 0.4f, 0.6f), 0.001f);
        float sine = MathF.Sin(elevation * MathF.PI / 180f);
        float mu0 = sine <= 0f ? sine : MathF.Sqrt(1f - (1f - sine * sine) / (1.333f * 1.333f));
        foreach (float distance in new[] { 0f, 0.05f, 0.30f, 2f, 16f })
            foreach (float offset in new[] { -0.05f, 0f, 0.05f })
            {
                Vector3 output = WaterColumnComposite.Transmitted(c, Bed, Vector3.One, Vector3.One,
                    1f, mu0, distance, Lit, 0.25f, offset);
                Assert.True(float.IsFinite(output.X) && float.IsFinite(output.Y) && float.IsFinite(output.Z));
                if (distance == 0f) Assert.Equal(Bed, output);
            }
    }
}
