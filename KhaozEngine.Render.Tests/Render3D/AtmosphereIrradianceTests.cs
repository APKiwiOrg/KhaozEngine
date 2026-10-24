using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class AtmosphereIrradianceTests : IClassFixture<AtmosphereLightingFixture>
{
    static readonly float[] Elevations = [5, 20, 45, 70, 89];
    static readonly float[] Azimuths = [0, 2f, -3.1f];
    static readonly float[] Heights = [0, .7f, 2, 150];
    readonly AtmosphereModel _earth;

    public AtmosphereIrradianceTests(AtmosphereLightingFixture fixture) => _earth = fixture.Earth;

    [Fact]
    public void DaylightUpwardIrradianceMatchesTheAmbient()
    {
        foreach (float height in Heights)
            foreach (float elevation in Elevations)
                foreach (float azimuth in Azimuths)
                {
                    var sun = Direction(elevation, azimuth);
                    var frame = new AtmosphereFrame(sun, -sun, 0, Vector3.One);
                    var lighting = _earth.EvaluateLighting(height, frame);
                    var upward = _earth.EvaluateIrradiance(height, frame).Evaluate(Vector3.UnitY);
                    Within(lighting.Ambient, upward, lighting.Ambient * 1e-3f,
                        $"Up at height {height}, elevation {elevation}, azimuth {azimuth}");
                    Assert.Equal(lighting, _earth.EvaluateLighting(height, frame));
                }
    }

    [Fact]
    public void DownwardIrradianceIsTheFillOrTheFloor()
    {
        var settings = AtmosphereSettings.Earth();
        var floor = settings.NightAmbientFloor * settings.NightTint;
        foreach (float height in Heights)
            foreach (float elevation in Elevations)
                foreach (float azimuth in Azimuths)
                {
                    var sun = Direction(elevation, azimuth);
                    var frame = new AtmosphereFrame(sun, -sun, 0, Vector3.One);
                    var lighting = _earth.EvaluateLighting(height, frame);
                    var downward = _earth.EvaluateIrradiance(height, frame).Evaluate(-Vector3.UnitY);
                    var expected = Vector3.Max(lighting.Fill, floor);
                    Within(expected, downward, expected * 1e-3f + new Vector3(1e-6f),
                        $"Down at height {height}, elevation {elevation}, azimuth {azimuth}");
                }
    }

    [Fact]
    public void TheSkyFollowsTheSunAzimuth()
    {
        var sun = Direction(20);
        var frame = new AtmosphereFrame(sun, -sun, 0, Vector3.One);
        var forward = _earth.EvaluateIrradiance(0, frame);
        var oppositeSun = Direction(20, MathF.PI);
        var reversed = _earth.EvaluateIrradiance(0,
            frame with { SunDirection = oppositeSun, MoonDirection = -oppositeSun });
        Assert.True(Luminance(forward.Evaluate(Vector3.UnitX)) > Luminance(forward.Evaluate(-Vector3.UnitX)));
        Within(forward.Evaluate(Vector3.UnitX), reversed.Evaluate(-Vector3.UnitX), new Vector3(1e-3f), "Mirrored +X");
        Within(forward.Evaluate(-Vector3.UnitX), reversed.Evaluate(Vector3.UnitX), new Vector3(1e-3f), "Mirrored -X");
    }

    [Fact]
    public void AzimuthWrapAndZenithAreContinuous()
    {
        var beforeSun = Direction(20, MathF.PI - 1e-4f);
        var afterSun = Direction(20, -MathF.PI + 1e-4f);
        var before = _earth.EvaluateIrradiance(0, new AtmosphereFrame(beforeSun, -beforeSun, 0, Vector3.One));
        var after = _earth.EvaluateIrradiance(0, new AtmosphereFrame(afterSun, -afterSun, 0, Vector3.One));
        var nearSun = Direction(89.99f, 2);
        var nearZenith = _earth.EvaluateIrradiance(0, new AtmosphereFrame(nearSun, -nearSun, 0, Vector3.One));
        var zenith = _earth.EvaluateIrradiance(0,
            new AtmosphereFrame(Vector3.UnitY, -Vector3.UnitY, 0, Vector3.One));
        foreach (var normal in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY,
            Vector3.UnitZ, -Vector3.UnitZ })
        {
            Within(before.Evaluate(normal), after.Evaluate(normal), new Vector3(1e-4f), "Azimuth wrap");
            var actual = zenith.Evaluate(normal);
            Assert.True(float.IsFinite(actual.X) && float.IsFinite(actual.Y) && float.IsFinite(actual.Z));
            Within(nearZenith.Evaluate(normal), actual, new Vector3(1e-3f), "Zenith");
        }
    }

    [Fact]
    public void NightFloorHoldsAtBothPoles()
    {
        var settings = AtmosphereSettings.Earth();
        var floor = settings.NightAmbientFloor * settings.NightTint;
        foreach (float height in Heights)
            foreach (float fraction in new[] { 0, .04f })
            {
                var sun = Direction(-30);
                var frame = new AtmosphereFrame(sun, -sun, fraction, Vector3.One);
                var irradiance = _earth.EvaluateIrradiance(height, frame);
                foreach (var normal in new[] { Vector3.UnitY, -Vector3.UnitY })
                {
                    var actual = irradiance.Evaluate(normal);
                    Assert.True(actual.X >= floor.X - 1e-6f && actual.Y >= floor.Y - 1e-6f
                        && actual.Z >= floor.Z - 1e-6f, $"Floor {floor}, actual {actual}");
                }
            }
    }

    [Fact]
    public void AmbientScaleScalesTheSkyAndFillScaleTheGround()
    {
        var settings = AtmosphereSettings.Earth();
        settings.NightAmbientFloor = 0;
        var model = new AtmosphereModel(settings);
        var sun = Direction(45, 2);
        var frame = new AtmosphereFrame(sun, -sun, 0, Vector3.One);
        var original = model.EvaluateIrradiance(.7f, frame);
        settings.AmbientScale = 2;
        Assert.True(model.UpdateSettings(settings));
        var doubled = model.EvaluateIrradiance(.7f, frame);
        var expectedUp = original.Evaluate(Vector3.UnitY) * 2;
        Within(expectedUp, doubled.Evaluate(Vector3.UnitY), expectedUp * 1e-3f, "Ambient scale");
        Assert.Equal(1, model.IrradianceBuildCount);

        settings.FillScale = 0;
        Assert.True(model.UpdateSettings(settings));
        var noGround = model.EvaluateIrradiance(.7f, frame);
        Within(Vector3.Zero, noGround.Evaluate(-Vector3.UnitY), new Vector3(1e-6f), "Zero fill");
        Within(expectedUp, noGround.Evaluate(Vector3.UnitY), expectedUp * 1e-3f, "Fill scale preserves sky");
        Assert.Equal(1, model.IrradianceBuildCount);
    }

    [Fact]
    public void APhysicalSettingChangeRebuildsTheTable()
    {
        var settings = AtmosphereSettings.Earth();
        var model = new AtmosphereModel(settings);
        var sun = Direction(45, 2);
        var frame = new AtmosphereFrame(sun, -sun, 0, Vector3.One);
        var original = model.EvaluateIrradiance(.7f, frame);
        int builds = model.IrradianceBuildCount;
        Assert.Equal(1, builds);

        settings.RayleighScatteringPerKm *= 2;
        Assert.True(model.UpdateSettings(settings));
        var rebuilt = model.EvaluateIrradiance(.7f, frame);

        Assert.Equal(builds + 1, model.IrradianceBuildCount);
        Assert.NotEqual(original, rebuilt);
        var fresh = new AtmosphereModel(settings).EvaluateIrradiance(.7f, frame);
        for (int i = 0; i < IrradianceSH.CoefficientCount; i++)
        {
            var expected = fresh.Coefficient(i);
            Within(expected, rebuilt.Coefficient(i), Vector3.Abs(expected) * 1e-5f,
                $"Rebuilt coefficient {i}");
        }
        Assert.Equal(rebuilt, model.EvaluateIrradiance(.7f, frame));
        Assert.Equal(builds + 1, model.IrradianceBuildCount);
    }

    [Fact]
    public void TheTableBuildsOnceAndOnlyOnDemand()
    {
        var model = new AtmosphereModel(AtmosphereSettings.Earth());
        var sun = Direction(45);
        var frame = new AtmosphereFrame(sun, -sun, 0, Vector3.One);
        var lighting = model.EvaluateLighting(0, frame);
        Assert.Equal(0, model.IrradianceBuildCount);
        var first = model.EvaluateIrradiance(0, frame);
        Assert.Equal(first, model.EvaluateIrradiance(0, frame));
        Assert.Equal(lighting, model.EvaluateLighting(0, frame));
        sun = Direction(70, 2);
        _ = model.EvaluateIrradiance(.7f, frame with { SunDirection = sun, MoonDirection = -sun });
        Assert.Equal(1, model.IrradianceBuildCount);
    }

    static Vector3 Direction(float elevationDegrees, float azimuth = 0)
    {
        float radians = elevationDegrees * MathF.PI / 180;
        return new Vector3(MathF.Cos(radians) * MathF.Cos(azimuth), MathF.Sin(radians),
            MathF.Cos(radians) * MathF.Sin(azimuth));
    }

    static float Luminance(Vector3 value) => Vector3.Dot(value, new Vector3(.2126f, .7152f, .0722f));

    static void Within(Vector3 expected, Vector3 actual, Vector3 tolerance, string context)
    {
        var error = Vector3.Abs(actual - expected);
        Assert.True(error.X <= tolerance.X && error.Y <= tolerance.Y && error.Z <= tolerance.Z,
            $"{context}: expected {expected}, actual {actual}, error {error}, tolerance {tolerance}");
    }
}
