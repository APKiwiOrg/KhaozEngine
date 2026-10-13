using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class AtmosphereModelTests
{
    [Fact]
    public void SphereAndDensity_IncludeGroundAndTent()
    {
        Assert.True(AtmosphereModel.IntersectSphere(new Vector3(0, 2, 0), -Vector3.UnitY, 1f, out var t));
        Assert.Equal(new Vector2(1, 3), t);
        var m = new AtmosphereModel(AtmosphereSettings.Earth());
        Assert.Equal(new Vector3(1, 1, 0), m.Density(0));
        Assert.Equal(m.Density(0), m.Density(-1));
        Assert.Equal(1f, m.Density(25).Z);
        Assert.Equal(0f, m.Density(10).Z);
        Assert.Equal(0f, m.Density(40).Z);
        Assert.Equal(.5f, m.Density(17.5f).Z);
        Assert.Equal(.5f, m.Density(32.5f).Z);
        Assert.InRange(Math.Abs(m.Density(8).X - Math.Exp(-1)), 0, 1e-7);
        Assert.InRange(Math.Abs(m.Density(1.2f).Y - Math.Exp(-1)), 0, 1e-7);
        Assert.Equal(Vector3.Zero, m.Transmittance(new Vector3(0, 6361, 0), -Vector3.UnitY));
        Assert.Equal(Vector3.One, m.Transmittance(new Vector3(0, 6461, 0), Vector3.UnitY));
        Assert.InRange(Vector3.Distance(m.Transmittance(new Vector3(0, 6360, 0), Vector3.UnitY),
            IndependentTransmittance(0, 1, 32768)), 0, .002f);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, .1)]
    [InlineData(2, .5)]
    [InlineData(25, 0)]
    public void EarthTransmittance_MatchesIndependentOracle(double heightKm, double mu)
    {
        var m = new AtmosphereModel(AtmosphereSettings.Earth());
        var direction = new Vector3((float)Math.Sqrt(1 - mu * mu), (float)mu, 0);
        var actual = m.Transmittance(new Vector3(0, (float)(6360 + heightKm), 0), direction);

        Assert.InRange(Vector3.Distance(actual, IndependentTransmittance(heightKm, mu, 32768)), 0, .002f);
    }

    [Fact]
    public void Transmittance_HandlesGroundAtmosphereEntryAndTangentContinuity()
    {
        var m = new AtmosphereModel(AtmosphereSettings.Earth());
        var surface = new Vector3(0, 6360, 0);
        var outward = m.Transmittance(surface, Vector3.UnitY);
        Assert.True(outward.X > 0 && outward.Y > 0 && outward.Z > 0);
        Assert.Equal(Vector3.Zero, m.Transmittance(surface, -Vector3.UnitY));
        Assert.Equal(Vector3.Zero, m.Transmittance(new Vector3(0, 6359, 0), Vector3.UnitY));
        Assert.Equal(Vector3.Zero, m.Transmittance(new Vector3(0, 6461, 0), -Vector3.UnitY));
        Assert.Equal(Vector3.One, m.Transmittance(new Vector3(0, 6461, 0), Vector3.UnitX));
        Assert.Equal(Vector3.One, m.Transmittance(new Vector3(0, 6460, 0), Vector3.UnitY));
        Assert.True(m.Transmittance(surface, Vector3.UnitX).X > 0);

        var tangentPosition = new Vector3(0, 6362, 0);
        var above = m.Transmittance(tangentPosition, new Vector3(1, 1e-5f, 0));
        var below = m.Transmittance(tangentPosition, new Vector3(1, -1e-5f, 0));
        Assert.True(above.X > 0 && below.X > 0);
        Assert.InRange(Vector3.Distance(above, below), 0, .005f);

        // A ray starting outside the shell must integrate only between its two atmosphere roots.
        var entryDirection = new Vector3(1, -.15f, 0);
        Assert.True(AtmosphereModel.IntersectSphere(new Vector3(0, 6470, 0), entryDirection, 6460, out var roots));
        Assert.True(roots.X > 0 && roots.Y > roots.X);
        var entry = new Vector3(0, 6470, 0) + Vector3.Normalize(entryDirection) * roots.X;
        var fromOutside = m.Transmittance(new Vector3(0, 6470, 0), entryDirection);
        Assert.InRange(Vector3.Distance(fromOutside, m.Transmittance(entry, entryDirection)), 0, .002f);
        Assert.InRange(fromOutside.X, .001f, .999f);
    }

    [Fact]
    public void SphereRoots_AreSignedOrderedDistancesForNormalizedDirections()
    {
        Assert.True(AtmosphereModel.IntersectSphere(Vector3.Zero, Vector3.UnitX * 7, 1, out var inside));
        Assert.Equal(new Vector2(-1, 1), inside);
        Assert.True(AtmosphereModel.IntersectSphere(new Vector3(0, 2, 0), Vector3.UnitY, 1, out var behind));
        Assert.Equal(new Vector2(-3, -1), behind);
        Assert.True(AtmosphereModel.IntersectSphere(new Vector3(1, 2, 0), -Vector3.UnitY, 1, out var tangent));
        Assert.Equal(new Vector2(2, 2), tangent);
        Assert.False(AtmosphereModel.IntersectSphere(new Vector3(2, 2, 0), -Vector3.UnitY, 1, out _));
    }

    [Fact]
    public void Transport_NormalizesFiniteDirectionsAndRejectsInvalidInputs()
    {
        var m = new AtmosphereModel(AtmosphereSettings.Earth());
        var position = new Vector3(0, 6362, 0);
        var expected = m.Transmittance(position, Vector3.UnitY);
        Assert.Equal(expected, m.Transmittance(position, Vector3.UnitY * 10));
        Assert.Equal(expected, m.Transmittance(position, Vector3.UnitY * float.MaxValue));
        Assert.Equal(expected, m.Transmittance(position, Vector3.UnitY * float.Epsilon));
        foreach (var invalid in new[] { Vector3.Zero, new Vector3(float.NaN, 0, 1), new Vector3(0, float.PositiveInfinity, 0) })
        {
            Assert.Throws<ArgumentException>(() => m.Transmittance(position, invalid));
            Assert.Throws<ArgumentException>(() => AtmosphereModel.IntersectSphere(position, invalid, 6460, out _));
        }
        Assert.Throws<ArgumentException>(() => m.Transmittance(new Vector3(float.NaN, 0, 0), Vector3.UnitY));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.Transmittance(position, Vector3.UnitY, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.Transmittance(position, Vector3.UnitY, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AtmosphereModel.IntersectSphere(position, Vector3.UnitY, 0, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.Density(float.NaN));
    }

    [Fact]
    public void SettingsUpdates_ValidateCompareValuesAndRetainIndependentSnapshots()
    {
        var settings = AtmosphereSettings.Earth();
        var m = new AtmosphereModel(settings);
        var originalDensity = m.Density(8);
        Assert.False(m.UpdateSettings(settings.Clone()));
        settings.RayleighScaleHeightKm = 16;
        Assert.Equal(originalDensity, m.Density(8));
        Assert.True(m.UpdateSettings(settings));
        Assert.InRange(Math.Abs(m.Density(8).X - Math.Exp(-.5)), 0, 1e-7);
        Assert.False(m.UpdateSettings(settings.Clone()));
        settings.RayleighScaleHeightKm = 4;
        Assert.InRange(Math.Abs(m.Density(8).X - Math.Exp(-.5)), 0, 1e-7);
        settings.RayleighScaleHeightKm = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => m.UpdateSettings(settings));
        Assert.InRange(Math.Abs(m.Density(8).X - Math.Exp(-.5)), 0, 1e-7);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AtmosphereModel(settings));
        Assert.Throws<ArgumentNullException>(() => new AtmosphereModel(null!));
        Assert.Throws<ArgumentNullException>(() => m.UpdateSettings(null!));
    }

    [Fact]
    public void Transport_UsesConfiguredExtinctionAndPlanetDimensions()
    {
        var settings = AtmosphereSettings.Earth();
        settings.PlanetRadiusKm = 100;
        settings.AtmosphereHeightKm = 20;
        settings.RayleighScatteringPerKm = Vector3.Zero;
        settings.MieScatteringPerKm = 0;
        settings.MieExtinctionPerKm = 0;
        settings.OzoneAbsorptionPerKm = Vector3.Zero;
        var m = new AtmosphereModel(settings);
        Assert.Equal(Vector3.One, m.Transmittance(new Vector3(0, 100, 0), Vector3.UnitY));
        Assert.Equal(Vector3.Zero, m.Transmittance(new Vector3(0, 101, 0), -Vector3.UnitY));
        settings.MieExtinctionPerKm = .1f;
        settings.MieScaleHeightKm = 5;
        Assert.True(m.UpdateSettings(settings));
        var expected = (float)Math.Exp(-.1 * 5 * (1 - Math.Exp(-4)));
        Assert.InRange(Vector3.Distance(new Vector3(expected), m.Transmittance(new Vector3(0, 100, 0), Vector3.UnitY)), 0, 1e-5f);
    }

    // Independent double sphere geometry and composite Simpson quadrature, with literal Earth coefficients.
    // No production density, mapping, roots or integration helpers participate in this oracle.
    static Vector3 IndependentTransmittance(double heightKm, double mu, int intervals)
    {
        double radius = 6360 + heightKm;
        double projection = radius * mu;
        double exit = -projection + Math.Sqrt(projection * projection - radius * radius + 6460 * 6460);
        double step = exit / intervals;
        double red = 0, green = 0, blue = 0;
        for (int i = 0; i <= intervals; i++)
        {
            double distance = i * step;
            double height = Math.Sqrt(radius * radius + distance * distance + 2 * projection * distance) - 6360;
            double rayleigh = Math.Exp(-Math.Max(height, 0) / 8);
            double mie = Math.Exp(-Math.Max(height, 0) / 1.2);
            double ozone = Math.Max(0, 1 - Math.Abs(height - 25) / 15);
            double weight = i == 0 || i == intervals ? 1 : i % 2 == 0 ? 2 : 4;
            red += weight * (.005802 * rayleigh + .004440 * mie + .000650 * ozone);
            green += weight * (.013558 * rayleigh + .004440 * mie + .001881 * ozone);
            blue += weight * (.0331 * rayleigh + .004440 * mie + .000085 * ozone);
        }
        return new Vector3((float)Math.Exp(-red * step / 3), (float)Math.Exp(-green * step / 3),
            (float)Math.Exp(-blue * step / 3));
    }
}
