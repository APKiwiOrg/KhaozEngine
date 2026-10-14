using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class AtmosphereFogHorizonTests
{
    [Theory]
    [InlineData(0f, 2f)]
    [InlineData(1f, 2f)]
    [InlineData(2f, 2f)]
    [InlineData(16f, 2f)]
    [InlineData(100f, 2f)]
    [InlineData(2000f, 2f)]
    [InlineData(0f, 45f)]
    [InlineData(1f, 45f)]
    [InlineData(2f, 45f)]
    [InlineData(16f, 45f)]
    [InlineData(100f, 45f)]
    [InlineData(2000f, 45f)]
    public void FogColour_HasNoJumpAtTheHorizon(float heightWorld, float sunDegrees)
        => AssertNoJumpAcrossTheHorizon(heightWorld, sunDegrees, 0);

    // The phase ratio varies below the horizon only. Toward, beside and away from a dawn sun it must start
    // from exactly the horizon colour.
    [Theory]
    [InlineData(2f, 0f)]
    [InlineData(2f, 30f)]
    [InlineData(2f, 90f)]
    [InlineData(2f, 180f)]
    [InlineData(16f, 0f)]
    [InlineData(16f, 30f)]
    [InlineData(16f, 90f)]
    [InlineData(16f, 180f)]
    [InlineData(100f, 0f)]
    [InlineData(100f, 30f)]
    [InlineData(100f, 90f)]
    [InlineData(100f, 180f)]
    public void DawnFogColour_HasNoJumpAtTheHorizonAtAnyAzimuth(float heightWorld, float azimuthDegrees)
        => AssertNoJumpAcrossTheHorizon(heightWorld, 6.37f, azimuthDegrees);

    static void AssertNoJumpAcrossTheHorizon(float heightWorld, float sunDegrees, float azimuthDegrees)
    {
        var settings = AtmosphereSettings.Earth();
        settings.Enabled = true;
        settings.HazeDensityPerKm = 100;
        // Keep every ray opaque even at 2 km so the band measures fog colour, not height-dependent transmission.
        settings.HazeScaleHeightKm = 100;
        var model = new AtmosphereModel(settings);
        float sunAngle = sunDegrees * MathF.PI / 180;
        var sun = new Vector3(MathF.Cos(sunAngle), MathF.Sin(sunAngle), 0);
        var frame = new AtmosphereFrame(sun, -sun, 0, Vector3.One);
        var eye = new Vector3(0, heightWorld, 0);
        double radius = settings.PlanetRadiusKm + heightWorld / 1000d;
        double horizon = -Math.Acos(settings.PlanetRadiusKm / radius);
        double azimuth = azimuthDegrees * Math.PI / 180;
        const double step = .0001 * Math.PI / 180;
        Vector3 Sample(double angle)
        {
            var view = new Vector3((float)(Math.Cos(angle) * Math.Cos(azimuth)), (float)Math.Sin(angle),
                (float)(Math.Cos(angle) * Math.Sin(azimuth)));
            return AtmosphereFog.Shade(settings, model, frame, Vector3.Zero, eye,
                eye + view * 100000, Vector3.Zero);
        }
        var below = Sample(horizon - step);
        var at = Sample(horizon);
        var above = Sample(horizon + step);
        float scale = MathF.Max(above.Length(), .01f);
        float jump = MathF.Max(Vector3.Distance(below, at), Vector3.Distance(at, above)) / scale;
        Console.WriteLine($"FOG_HORIZON height={heightWorld} sun={sunDegrees} azimuth={azimuthDegrees} below={below} at={at} above={above} relativeJump={jump}");
        Assert.True(jump < .001f, $"Fog colour jumps by {jump} over a {step} radian step at height {heightWorld}.");
        const double bandStep = .01 * Math.PI / 180;
        // Sweep the reported eye-level line. The tiny geometric-horizon check above is separate at altitude.
        var previous = Sample(-.5 * Math.PI / 180);
        for (int i = 1; i <= 100; i++)
        {
            var next = Sample((-.5 + i * .01) * Math.PI / 180);
            float relativeChange = Vector3.Distance(previous, next) / MathF.Max(next.Length(), .01f);
            Assert.True(relativeChange < 75 * bandStep,
                $"Fog colour changes by {relativeChange} over {bandStep} radians at sample {i}.");
            previous = next;
        }
    }

    [Theory]
    [InlineData(2f)]
    [InlineData(45f)]
    public void FogColour_ApproachesTheConstantDownwardColourWithZeroSlope(float sunDegrees)
    {
        var settings = AtmosphereSettings.Earth();
        settings.Enabled = true;
        settings.HazeDensityPerKm = 100;
        var model = new AtmosphereModel(settings);
        float sunAngle = sunDegrees * MathF.PI / 180;
        var sun = new Vector3(MathF.Cos(sunAngle), MathF.Sin(sunAngle), 0);
        var frame = new AtmosphereFrame(sun, -sun, 0, Vector3.One);
        const float step = .0001f * MathF.PI / 180;
        var at = AtmosphereFog.Shade(settings, model, frame, Vector3.Zero, Vector3.Zero,
            new Vector3(100000, 0, 0), Vector3.Zero);
        var above = AtmosphereFog.Shade(settings, model, frame, Vector3.Zero, Vector3.Zero,
            new Vector3(MathF.Cos(step), MathF.Sin(step), 0) * 100000, Vector3.Zero);
        float slope = Vector3.Distance(at, above) / (step * at.Length());
        Console.WriteLine($"FOG_HORIZON_SLOPE sun={sunDegrees} relativeSlopePerRadian={slope}");
        Assert.True(slope < .01f, $"The upward fog colour slope {slope} must meet the geometric-horizon colour.");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.001f)]
    [InlineData(.002f)]
    [InlineData(.016f)]
    [InlineData(.1f)]
    [InlineData(2f)]
    public void FogLookup_StaysInTheSkyHemisphereAcrossTheHorizon(float heightKm)
    {
        float radius = 6360 + heightKm;
        double horizon = -Math.Acos(6360d / radius);
        for (int i = 0; i <= 100; i++)
        {
            double elevation = horizon + (-.5 + i * .01) * Math.PI / 180;
            var view = new Vector3((float)Math.Cos(elevation), (float)Math.Sin(elevation), 0);
            var uv = AtmosphereFogSampling.SkyUv(view, radius, 6360);
            Assert.InRange(uv.Y, .5f / 108, 53.5f / 108);
        }
        Assert.Equal(new Vector2(0, 53.5f / 108), AtmosphereFogSampling.SkyUv(-Vector3.UnitY, radius, 6360));
        Assert.Equal(new Vector2(0, .5f / 108), AtmosphereFogSampling.SkyUv(Vector3.UnitY, radius, 6360));
    }

    [Fact]
    public void FinalSkyTexel_JoinsTheOrdinaryMappingWithMatchingSlope()
    {
        double join = Math.PI / (2 * 53 * 53);
        const double step = 1e-7;
        float Sample(double elevation) => AtmosphereFogSampling.SkyUv(
            new Vector3((float)Math.Cos(elevation), (float)Math.Sin(elevation), 0), 6360, 6360).Y;
        float leftSlope = (Sample(join) - Sample(join - step)) / (float)step;
        float rightSlope = (Sample(join + step) - Sample(join)) / (float)step;
        Assert.InRange(MathF.Abs(leftSlope - rightSlope) / MathF.Abs(rightSlope), 0, .01f);
        var upward = Vector3.Normalize(new Vector3(1, 1, 0));
        Assert.InRange(Vector2.Distance(AtmosphereLutMapping.SkyUv(upward, 6360, 6360),
            AtmosphereFogSampling.SkyUv(upward, 6360, 6360)), 0, 1e-7f);
    }
}
