using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Phase-correct downward fog in-scatter, checked against an independent saturated slab of the
/// same Earth air at the true scattering angle (the consumer investigation's reference, KhaozEngine #1406).</summary>
public sealed class AtmosphereFogPhaseTests
{
    const float CameraY = 16.5f;
    // The sky-view sample and the slab agree within about 7 percent of luma at the horizon. The ratio keeps
    // that horizon error and must not add to it below the horizon.
    const double SlabLumaTolerance = .08;

    [Theory]
    [InlineData(2f)]
    [InlineData(CameraY)]
    [InlineData(100f)]
    [InlineData(2000f)]
    public void Ratio_IsExactlyOneAtAndAboveTheHorizon(float heightWorld)
    {
        var s = Settings();
        var model = new AtmosphereModel(s);
        var frame = Frame(6.37f, moonFraction: .04f);
        float heightKm = heightWorld / 1000;
        double radius = s.PlanetRadiusKm + (double)heightKm;
        var scattering = AtmosphereFogScattering.Evaluate(s, model, frame, heightKm);
        double tangent = -Math.Sqrt((radius - s.PlanetRadiusKm) * (radius + s.PlanetRadiusKm)) / radius;
        double horizon = Math.Asin(tangent);
        var eye = new Vector3(0, heightWorld, 0);
        for (int a = 0; a < 12; a++)
        {
            double azimuth = a * Math.PI / 6;
            var atHorizon = new Vector3((float)(Math.Sqrt(1 - tangent * tangent) * Math.Cos(azimuth)), (float)tangent,
                (float)(Math.Sqrt(1 - tangent * tangent) * Math.Sin(azimuth)));
            Assert.Equal(Vector3.One, scattering.Ratio(atHorizon, radius, s.PlanetRadiusKm));
            foreach (double above in new[] { 1e-4, .01, .1, .5, 1.2, Math.PI / 2 - horizon })
            {
                var view = Direction(azimuth, horizon + above);
                Assert.Equal(Vector3.One, scattering.Ratio(view, radius, s.PlanetRadiusKm));
                var expected = model.SkyViewValue(heightKm, AtmosphereFogSampling.SkyUv(view, radius, s.PlanetRadiusKm), frame);
                var shaded = AtmosphereFog.Shade(s, model, frame, Vector3.Zero, eye, eye + view * 1e5f, Vector3.Zero);
                Assert.InRange(Vector3.Distance(expected, shaded), 0, 1e-5f * expected.Length());
            }
        }
    }

    [Fact]
    public void Ratio_DimsDownwardViewsTowardALowSunAndKeepsTheSide()
    {
        var s = Settings();
        var model = new AtmosphereModel(s);
        var frame = Frame(6.37f);
        float heightKm = CameraY / 1000;
        double radius = s.PlanetRadiusKm + (double)heightKm;
        var scattering = AtmosphereFogScattering.Evaluate(s, model, frame, heightKm);
        float previous = 1;
        foreach (double elevation in new[] { -5.0, -10, -20, -30, -45, -60 })
        {
            var toward = scattering.Ratio(Direction(0, Radians(elevation)), radius, s.PlanetRadiusKm);
            Assert.True(toward.X < 1 && toward.Y < 1 && toward.Z < 1, $"Ratio {toward} at {elevation} toward the sun.");
            Assert.True(Luma(toward) < previous, $"Ratio {toward} at {elevation} must fall with depression toward the sun.");
            previous = Luma(toward);
            var side = scattering.Ratio(Direction(Math.PI / 2, Radians(elevation)), radius, s.PlanetRadiusKm);
            Assert.InRange(Vector3.Distance(side, Vector3.One), 0, .03f);
        }
        Assert.InRange(previous, 0, .2f);
    }

    [Theory]
    [InlineData(6.37f)]
    [InlineData(9.59f)]
    [InlineData(53f)]
    public void DownwardFog_MatchesTheSlabAtTheTrueAngle(float sunDegrees)
    {
        var s = Settings();
        var model = new AtmosphereModel(s);
        var frame = Frame(sunDegrees);
        var eye = new Vector3(0, CameraY, 0);
        foreach (double offset in new[] { 0.0, 30, 90 })
            foreach (double elevation in new[] { -10.0, -20, -30, -45, -60 })
            {
                var view = Direction(Radians(offset), Radians(elevation));
                var engine = AtmosphereFog.Shade(s, model, frame, Vector3.Zero, eye, eye + view * 1e5f, Vector3.Zero);
                var slab = Slab(s, model, frame, view, CameraY / 1000);
                double error = Luma(engine) / Luma(slab) - 1;
                Console.WriteLine($"FOG_SLAB sun={sunDegrees} offset={offset} elevation={elevation} engine={engine} slab={slab} lumaError={error:F4}");
                Assert.InRange(error, -SlabLumaTolerance, SlabLumaTolerance);
            }
    }

    [Fact]
    public void DownwardFog_FollowsTheMoonKeyAtNight()
    {
        var s = Settings();
        var model = new AtmosphereModel(s);
        var sun = Direction(Math.PI, Radians(-30));
        var moon = Direction(0, Radians(8));
        var frame = new AtmosphereFrame(sun, moon, 1, Vector3.One, KeyLightSource.Moon);
        var eye = new Vector3(0, CameraY, 0);
        float heightKm = CameraY / 1000;
        double radius = s.PlanetRadiusKm + (double)heightKm;
        var scattering = AtmosphereFogScattering.Evaluate(s, model, frame, heightKm);
        foreach (double elevation in new[] { -10.0, -30, -60 })
        {
            var view = Direction(0, Radians(elevation));
            var ratio = scattering.Ratio(view, radius, s.PlanetRadiusKm);
            Assert.True(Luma(ratio) < .7f, $"The moon aureole must leave downward views at {elevation}, ratio {ratio}.");
            var engine = AtmosphereFog.Shade(s, model, frame, Vector3.Zero, eye, eye + view * 1e5f, Vector3.Zero);
            double error = Luma(engine) / Luma(Slab(s, model, frame, view, heightKm)) - 1;
            Console.WriteLine($"FOG_SLAB_MOON elevation={elevation} engine={engine} lumaError={error:F4}");
            Assert.InRange(error, -SlabLumaTolerance, SlabLumaTolerance);
        }
    }

    [Theory]
    [InlineData(2f, 0.0)]
    [InlineData(2f, 30.0)]
    [InlineData(CameraY, 0.0)]
    [InlineData(100f, 0.0)]
    public void DownwardFog_LeavesTheHorizonWithZeroSlopeAndJoinsTheTrueAngleSmoothly(float heightWorld, double azimuthDegrees)
    {
        var s = Settings();
        var model = new AtmosphereModel(s);
        var frame = Frame(2);
        var eye = new Vector3(0, heightWorld, 0);
        double radius = s.PlanetRadiusKm + heightWorld / 1000d;
        double horizon = -Math.Acos(s.PlanetRadiusKm / radius);
        double cell = (Math.PI / 2 - horizon) / (53 * 53);
        double azimuth = Radians(azimuthDegrees);
        Vector3 Sample(double depression) => AtmosphereFog.Shade(s, model, frame, Vector3.Zero, eye,
            eye + Direction(azimuth, horizon - depression) * 1e5f, Vector3.Zero);
        double step = cell / 64;
        var previous = Sample(0);
        var previousSlope = Vector3.Zero;
        for (int i = 1; i <= 192; i++)
        {
            var next = Sample(i * step);
            var slope = (next - previous) / ((float)step * MathF.Max(next.Length(), .01f));
            if (i == 1) Assert.InRange(slope.Length(), 0, .01f);
            Assert.True(Vector3.Distance(previousSlope, slope) < 3,
                $"Downward fog slope jumps from {previousSlope} to {slope} at {i} sixty-fourths of a cell.");
            previous = next;
            previousSlope = slope;
        }
        // Past the eased cell the colour follows the true angle.
        var view = Direction(azimuth, horizon - 3 * cell);
        var scattering = AtmosphereFogScattering.Evaluate(s, model, frame, heightWorld / 1000);
        Assert.True(AtmosphereFogSampling.BelowHorizon(view, radius, s.PlanetRadiusKm, out _, out var eased));
        Assert.InRange(Vector3.Distance(Vector3.Normalize(view), eased), 0, 1e-6f);
        Assert.NotEqual(Vector3.One, scattering.Ratio(view, radius, s.PlanetRadiusKm));
    }

    [Fact]
    public void Vacuum_KeepsTheSkySampleBelowTheHorizon()
    {
        var s = Settings();
        s.RayleighScatteringPerKm = Vector3.Zero;
        s.MieScatteringPerKm = 0;
        var model = new AtmosphereModel(s);
        var scattering = AtmosphereFogScattering.Evaluate(s, model, Frame(6.37f), CameraY / 1000);
        Assert.Equal(Vector3.One, scattering.Ratio(Direction(0, Radians(-30)), s.PlanetRadiusKm + .0165, s.PlanetRadiusKm));
        Assert.Equal(Vector3.One, scattering.Ratio(-Vector3.UnitY, s.PlanetRadiusKm + .0165, s.PlanetRadiusKm));
    }

    [Fact]
    public void Nadir_UsesThePlusXHorizonSample()
    {
        var s = Settings();
        var model = new AtmosphereModel(s);
        var scattering = AtmosphereFogScattering.Evaluate(s, model, Frame(6.37f), CameraY / 1000);
        double radius = s.PlanetRadiusKm + .0165;
        var nadir = scattering.Ratio(-Vector3.UnitY, radius, s.PlanetRadiusKm);
        var nearNadir = scattering.Ratio(Vector3.Normalize(new Vector3(1e-4f, -1, 0)), radius, s.PlanetRadiusKm);
        Assert.InRange(Vector3.Distance(nadir, nearNadir), 0, 1e-3f);
    }

    // Saturated in-scatter of a homogeneous slab at the camera's scaled altitude: each source's transmittance
    // times phase-weighted Rayleigh and Mie scattering, plus its multiple-scattering source, over extinction.
    static Vector3 Slab(AtmosphereSettings s, AtmosphereModel model, in AtmosphereFrame frame, Vector3 view, float heightKm)
    {
        double scaled = heightKm * s.AerialDistanceScale;
        double rayleighDensity = Math.Exp(-scaled / s.RayleighScaleHeightKm);
        double mieDensity = Math.Exp(-scaled / s.MieScaleHeightKm);
        double mie = s.MieScatteringPerKm * mieDensity, grey = s.MieExtinctionPerKm * mieDensity;
        var position = new Vector3(0, s.PlanetRadiusKm + heightKm, 0);
        var result = Vector3.Zero;
        foreach ((Vector3 direction, float weight) in new[] { (frame.SunDirection, 1f), (frame.MoonDirection, frame.MoonFraction) })
        {
            if (weight == 0) continue;
            var source = Vector3.Normalize(direction);
            var transmittance = model.Transmittance(position, source);
            var multiple = model.MultiScattering(heightKm, source.Y);
            double mu = Vector3.Dot(view, source);
            double Channel(float rayleigh, float transmit, float psi)
            {
                double r = rayleigh * rayleighDensity;
                return (transmit * (r * RayleighPhase(mu) + mie * MiePhase(mu, s.MieAnisotropy)) + psi * (r + mie)) / (r + grey);
            }
            result += weight * new Vector3((float)Channel(s.RayleighScatteringPerKm.X, transmittance.X, multiple.X),
                (float)Channel(s.RayleighScatteringPerKm.Y, transmittance.Y, multiple.Y),
                (float)Channel(s.RayleighScatteringPerKm.Z, transmittance.Z, multiple.Z));
        }
        return result * (s.SunIlluminance * MathF.PI);
    }

    static double RayleighPhase(double mu) => 3 * (1 + mu * mu) / (16 * Math.PI);

    static double MiePhase(double mu, double g)
        => 3 * (1 - g * g) * (1 + mu * mu) / (8 * Math.PI * (2 + g * g) * Math.Pow(1 + g * g - 2 * g * mu, 1.5));

    static AtmosphereSettings Settings()
    {
        var settings = AtmosphereSettings.Earth();
        settings.Enabled = true;
        // Opaque haze isolates the fog colour. Haze extinguishes only and never enters the colour.
        settings.HazeDensityPerKm = 100;
        settings.HazeScaleHeightKm = 100;
        settings.NightSkyFloor = 0;
        return settings;
    }

    static AtmosphereFrame Frame(float sunDegrees, float moonFraction = 0)
    {
        var sun = Direction(0, Radians(sunDegrees));
        return new AtmosphereFrame(sun, -sun, moonFraction, Vector3.One);
    }

    static Vector3 Direction(double azimuth, double elevation)
        => new((float)(Math.Cos(elevation) * Math.Cos(azimuth)), (float)Math.Sin(elevation),
            (float)(Math.Cos(elevation) * Math.Sin(azimuth)));

    static double Radians(double degrees) => degrees * Math.PI / 180;

    static float Luma(Vector3 c) => .2126f * c.X + .7152f * c.Y + .0722f * c.Z;
}
