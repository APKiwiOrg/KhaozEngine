using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class AtmosphereFogOnsetTests
{
    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(1f, 16.5f, 16.5f)]
    [InlineData(25f, 16.5f, 0f)]
    [InlineData(550f, 1.7f, 1.7f)]
    [InlineData(1e6f, 2000f, 0f)]
    public void ZeroOnset_IsBitIdenticalToTheLinearDistance(float distance, float startY, float endY)
    {
        var s = Shipped();
        Assert.Equal(0f, s.AerialOnsetDistance);
        double k = (double)s.AerialDistanceScale / s.WorldUnitsPerKilometre;
        double km = distance * k, start = Math.Max((double)startY, 0) * k, end = Math.Max((double)endY, 0) * k;
        double rayleighDepth = AtmosphereFog.OpticalDepthTerm(km, start, end, s.RayleighScaleHeightKm);
        double grey = s.MieExtinctionPerKm * AtmosphereFog.OpticalDepthTerm(km, start, end, s.MieScaleHeightKm)
            + s.HazeDensityPerKm * AtmosphereFog.OpticalDepthTerm(km, start, end, s.HazeScaleHeightKm);
        var expected = new Vector3((float)Math.Exp(-(s.RayleighScatteringPerKm.X * rayleighDepth + grey)),
            (float)Math.Exp(-(s.RayleighScatteringPerKm.Y * rayleighDepth + grey)),
            (float)Math.Exp(-(s.RayleighScatteringPerKm.Z * rayleighDepth + grey)));
        Assert.Equal(expected, AtmosphereFog.Transmittance(s, distance, startY, endY));
        foreach (double d in new[] { 0, 1e-30, 1, 80, 1e30, double.PositiveInfinity })
            Assert.Equal(d, AtmosphereFog.OnsetDistance(d, 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(80)]
    public void OnsetDistance_HasZeroValueAndSlopeAtTheCamera(double onset)
    {
        Assert.Equal(0d, AtmosphereFog.OnsetDistance(0, onset));
        foreach (double h in new[] { 1e-3, 1e-5, 1e-7 })
        {
            double slope = AtmosphereFog.OnsetDistance(h, onset) / h;
            Assert.InRange(slope, 0, 2 * h / onset);
        }
        var s = Shipped();
        s.AerialOnsetDistance = (float)onset;
        Assert.Equal(Vector3.One, AtmosphereFog.Transmittance(s, 0, 16.5f, 16.5f));
        var near = Vector3.One - AtmosphereFog.Transmittance(s, 1e-3f, 16.5f, 16.5f);
        Assert.InRange(MathF.Max(near.X, MathF.Max(near.Y, near.Z)), 0, 1e-6f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(80)]
    public void OnsetDistance_IsMonotoneBelowTheDistanceAndTendsToItLessTheOnset(double onset)
    {
        double previous = 0;
        for (double d = 1e-6; d < 1e12; d *= 1.1)
        {
            double optical = AtmosphereFog.OnsetDistance(d, onset);
            Assert.True(optical >= previous, $"Optical distance fell from {previous} to {optical} at {d}.");
            Assert.True(optical <= d, $"Optical distance {optical} exceeds {d}.");
            Assert.True(optical >= Math.Max(d - onset, 0) - 1e-15 * d, $"Optical distance {optical} is below {d} - {onset}.");
            previous = optical;
        }
        double far = 1e9;
        Assert.InRange(far - AtmosphereFog.OnsetDistance(far, onset) - onset, -1e-3, 1e-3);
        Assert.Equal(double.PositiveInfinity, AtmosphereFog.OnsetDistance(double.PositiveInfinity, onset));
    }

    [Fact]
    public void Onset_ClearsTheNearFieldAndKeepsTheFarField()
    {
        var s = Shipped();
        var linear = AtmosphereFog.Transmittance(s, 25, 1.7f, 1.7f);
        var linearFar = AtmosphereFog.Transmittance(s, 550, 1.7f, 1.7f);
        s.AerialOnsetDistance = 80;
        var near = AtmosphereFog.Transmittance(s, 25, 1.7f, 1.7f);
        var far = AtmosphereFog.Transmittance(s, 550, 1.7f, 1.7f);
        Assert.True(Amount(near) < Amount(linear) / 3, $"Onset fog {Amount(near)} against linear {Amount(linear)} at 25 m.");
        Assert.True(Amount(far) > Amount(linearFar) - .05f, $"Onset fog {Amount(far)} against linear {Amount(linearFar)} at 550 m.");
        // The onset is the optical distance itself, so heights keep their meaning.
        var shifted = Shipped();
        Assert.InRange(Vector3.Distance(far, AtmosphereFog.Transmittance(shifted,
            (float)AtmosphereFog.OnsetDistance(550, 80), 1.7f, 1.7f)), 0, 1e-6f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(16.5f)]
    [InlineData(100f)]
    public void HorizontalRays_StillReachZeroTransmittanceWithAnOnset(float height)
    {
        var s = Shipped();
        s.AerialOnsetDistance = 80;
        Assert.Equal(Vector3.Zero, AtmosphereFog.Transmittance(s, float.PositiveInfinity, height, height));
        float previous = 1;
        for (float d = 100; d < 1e8f; d *= 4)
        {
            float amount = 1 - Amount(AtmosphereFog.Transmittance(s, d, height, height));
            Assert.True(amount <= previous, $"Transmittance rose to {amount} at {d}.");
            previous = amount;
        }
        Assert.InRange(previous, 0, 1e-6f);
    }

    [Fact]
    public void Shade_UsesTheOnsetTransmittance()
    {
        var s = Shipped();
        s.AerialOnsetDistance = 80;
        var model = new AtmosphereModel(s);
        var frame = new AtmosphereFrame(Vector3.UnitY, -Vector3.UnitY, 0, Vector3.One);
        var eye = new Vector3(0, 1.7f, 0);
        var black = AtmosphereFog.Shade(s, model, frame, Vector3.Zero, eye, eye + new Vector3(25, 0, 0), Vector3.Zero);
        var white = AtmosphereFog.Shade(s, model, frame, Vector3.One, eye, eye + new Vector3(25, 0, 0), Vector3.Zero);
        var transmittance = AtmosphereFog.Transmittance(s, 25, 1.7f, 1.7f);
        Assert.InRange(Vector3.Distance(white - black, transmittance), 0, 1e-6f);
    }

    static float Amount(Vector3 transmittance) => 1 - (transmittance.X + transmittance.Y + transmittance.Z) / 3;

    // The consumer look check's coefficients: Earth air at the default aerial scale with a 0.3 per km haze.
    static AtmosphereSettings Shipped()
    {
        var settings = AtmosphereSettings.Earth();
        settings.Enabled = true;
        settings.HazeDensityPerKm = .3f;
        return settings;
    }
}
