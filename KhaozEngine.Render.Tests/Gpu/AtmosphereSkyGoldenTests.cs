using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class AtmosphereSkyGoldenTests : IClassFixture<AtmosphereGoldenScene>
{
    readonly AtmosphereGoldenScene _fixture;

    public AtmosphereSkyGoldenTests(AtmosphereGoldenScene fixture) => _fixture = fixture;

    [GpuFact]
    public void Golden_AtmosphereNoon() => Golden("scene3d_atmosphere_noon", .5f);

    [GpuFact]
    public void Golden_AtmosphereSunset() => Golden("scene3d_atmosphere_sunset", .747f);

    [GpuFact]
    public void Golden_AtmosphereMoonlitNight() => Golden("scene3d_atmosphere_moonlit", 0);

    [GpuFact]
    public void Golden_IndependentMoonAndAzimuthSeam()
    {
        byte[] actual = MoonCapture(mirrored: false);
        byte[] mirrored = MoonCapture(mirrored: true);
        Assert.True(CentreBrightness(actual) > CentreBrightness(mirrored) + .1f,
            "The independent moon must brighten its actual direction, rather than the opposite-sun direction.");

        byte[] left = SeamCapture(-1e-4f);
        byte[] right = SeamCapture(1e-4f);
        AssertGridsClose(left, right, .01f);
    }

    [GpuFact]
    public void Golden_PhysicalSkyIgnoresScreenHorizonAndStylizedAnchor()
    {
        byte[] screen = _fixture.Capture(scene =>
        {
            TimeAndCamera(scene, .747f);
            scene.Post.Sky.Horizon = SkyHorizon.Screen;
            scene.Post.Sky.Anchor = SunAnchor.StylizedBackdrop;
        });
        byte[] world = _fixture.Capture(scene =>
        {
            TimeAndCamera(scene, .747f);
            scene.Post.Sky.Horizon = SkyHorizon.World;
            scene.Post.Sky.Anchor = SunAnchor.World;
        });
        Assert.Equal(world, screen);
    }

    [GpuFact]
    public void Golden_SharedFixtureMatchesFreshScene()
    {
        _fixture.Capture(.5f);
        _fixture.Capture(0);
        _fixture.Capture(.747f);
        AssertGridsClose(_fixture.CaptureFresh(.5f), _fixture.Capture(.5f), GoldenCompare.InSessionTolerance);
    }

    [GpuFact]
    public void Golden_DefaultOffAndToggleOffAreByteIdentical()
    {
        byte[] before = _fixture.Capture(scene => scene.Post.Atmosphere = new AtmosphereSettings());
        _fixture.Capture(scene =>
        {
            scene.Post.Atmosphere.MieScatteringPerKm *= 1.05f;
            scene.Post.Atmosphere.DayTint = new Vector3(.8f, .9f, 1);
        });
        Assert.Equal(before, _fixture.Capture(.5f, atmosphere: false));
        Assert.Equal(before, _fixture.CaptureFresh(.5f, atmosphere: false));
    }

    [GpuFact]
    public void Golden_OneShotFrameMeasurement()
    {
        if (Environment.GetEnvironmentVariable("KE_MEASURE_ATMOSPHERE") != "1") return;
        using var fixture = new AtmosphereGoldenScene();
        double off = fixture.MeasureSingleFrame(false);
        double on = fixture.MeasureSingleFrame(true);
        Console.WriteLine($"ATMOSPHERE_FRAME off_ms={off:F6} on_ms={on:F6} delta_ms={on - off:F6} "
            + $"cold_cache_ms={fixture.MeasurementColdCacheMilliseconds:F6} size=1600x900");
        Assert.True(double.IsFinite(off) && off > 0 && double.IsFinite(on) && on > 0);
    }

    [GpuFact]
    public void Golden_PhysicalSkyReflectsBothBodies()
    {
        FlyCamera3D camera = null!;
        Vector3 sun = default, moon = default;
        byte[] rgba = _fixture.CaptureWater(scene =>
        {
            _fixture.TimeOfDay = .7f;
            _fixture.Cycle.SolarDeclinationDegrees = -15;
            _fixture.Cycle.MoonDeclinationDegrees = 15;
            _fixture.Cycle.MoonHourOffset = .5f;
            // Face both bodies into the authored sea footprint rather than past its near edge.
            _fixture.Cycle.HeadingDegrees = 90;
            var state = SunCycle.Evaluate(_fixture.TimeOfDay, _fixture.Cycle);
            sun = -state.LightDirection;
            moon = -state.MoonDirection;
            Assert.True(sun.Y > 0 && moon.Y > 0, "Both physical bodies must be above the horizon.");
            camera = (FlyCamera3D)scene.CameraOverride!;
            camera.Position = new Vector3(0, 1.6f, 9);
            Aim(camera, new Vector3(sun.X + moon.X, -.1f, sun.Z + moon.Z));
            // Enlarge the angular discs so the seven-pixel patches lie inside them on every backend.
            scene.Post.Atmosphere.SunAngularRadiusRadians = .075f;
            scene.Post.Sky.SunRadius = .075f;
            WaterSceneTuning.ApplyCalmMirror(scene.Post.Water);
        });

        foreach ((Vector3 direction, string name) in new[] { (sun, "sun"), (moon, "moon") })
        {
            var sky = Pixel(camera, direction);
            var sea = Pixel(camera, new Vector3(direction.X, -direction.Y, direction.Z));
            Assert.True(sea.y > sky.y, $"The {name} reflection must lie below its sky disc.");
            Vector3 inSky = AveragePatch(rgba, sky.x, sky.y, 3);
            Vector3 skyControl = AveragePatch(rgba, AtmosphereGoldenScene.Width / 2, sky.y, 3);
            Assert.True(Vector3.Distance(inSky, skyControl) > .2f, $"The physical {name} disc must be visible.");
            Vector3 inSea = AveragePatch(rgba, sea.x, sea.y, 3);
            Vector3 control = AveragePatch(rgba, AtmosphereGoldenScene.Width / 2, sea.y, 3);
            Assert.True(Vector3.Distance(inSea, inSky) < Vector3.Distance(control, inSky) * .85f,
                $"The {name} reflection at ({sea.x},{sea.y}) must be at least 15 percent closer to its sky colour than open water.");
        }
    }

    static (int x, int y) Pixel(FlyCamera3D camera, Vector3 direction)
    {
        Assert.True(SkyMath.ProjectSunWorldToNdc(camera.View, camera.Projection, direction, out Vector2 ndc));
        Assert.InRange(ndc.X, -.98f, .98f);
        Assert.InRange(ndc.Y, -.98f, .98f);
        return ((int)((ndc.X * .5f + .5f) * AtmosphereGoldenScene.Width),
            (int)((.5f - ndc.Y * .5f) * AtmosphereGoldenScene.Height));
    }

    // Same top-origin, seven-pixel patches as SkyWorldHorizonGoldenTests, without a new golden grid.
    static Vector3 AveragePatch(byte[] rgba, int cx, int cy, int half)
    {
        Vector3 sum = Vector3.Zero;
        int count = 0;
        for (int y = Math.Max(0, cy - half); y <= Math.Min(AtmosphereGoldenScene.Height - 1, cy + half); y++)
            for (int x = Math.Max(0, cx - half); x <= Math.Min(AtmosphereGoldenScene.Width - 1, cx + half); x++)
            {
                int i = (y * AtmosphereGoldenScene.Width + x) * 4;
                sum += new Vector3(rgba[i], rgba[i + 1], rgba[i + 2]) / 255;
                count++;
            }
        return sum / count;
    }

    // Compare celestial directions without a lit ground receiver in the below-horizon control view.
    byte[] MoonCapture(bool mirrored) => _fixture.CaptureEffects(scene =>
    {
        _fixture.TimeOfDay = .4f;
        _fixture.Cycle.MoonHourOffset = 6;
        _fixture.Cycle.MoonDeclinationDegrees = 20;
        _fixture.Cycle.HeadingDegrees = 35;
        var state = SunCycle.Evaluate(.4f, _fixture.Cycle);
        var actual = -state.MoonDirection;
        var oppositeSun = state.LightDirection;
        Aim((FlyCamera3D)scene.CameraOverride!, mirrored ? oppositeSun : actual);
    }, _ => { });

    byte[] SeamCapture(float azimuth) => _fixture.Capture(scene =>
    {
        _fixture.TimeOfDay = .747f;
        Aim((FlyCamera3D)scene.CameraOverride!, new Vector3(MathF.Cos(azimuth), .15f, MathF.Sin(azimuth)));
    });

    void TimeAndCamera(Scene3D scene, float time)
    {
        _fixture.TimeOfDay = time;
        ((FlyCamera3D)scene.CameraOverride!).Yaw = -MathF.PI / 2;
    }

    static void Aim(FlyCamera3D camera, Vector3 direction)
    {
        var d = Vector3.Normalize(direction);
        camera.Yaw = MathF.Atan2(d.X, d.Z);
        camera.Pitch = MathF.Asin(d.Y);
    }

    static float CentreBrightness(byte[] rgba)
    {
        float sum = 0;
        for (int y = AtmosphereGoldenScene.Height / 2 - 1; y <= AtmosphereGoldenScene.Height / 2; y++)
            for (int x = AtmosphereGoldenScene.Width / 2 - 1; x <= AtmosphereGoldenScene.Width / 2; x++)
            {
                int i = (y * AtmosphereGoldenScene.Width + x) * 4;
                sum += (rgba[i] + rgba[i + 1] + rgba[i + 2]) / (3f * 255);
            }
        return sum / 4;
    }

    static void AssertGridsClose(byte[] expected, byte[] actual, float tolerance)
    {
        var a = GoldenCompare.Downsample(expected, AtmosphereGoldenScene.Width, AtmosphereGoldenScene.Height);
        var b = GoldenCompare.Downsample(actual, AtmosphereGoldenScene.Width, AtmosphereGoldenScene.Height);
        for (int i = 0; i < a.Length; i++) Assert.InRange(MathF.Abs(a[i] - b[i]), 0, tolerance);
    }

    void Golden(string name, float time)
    {
        byte[] rgba = _fixture.Capture(time);
        AtmosphereGoldenScene.WriteCapture(name, rgba);
        var upper = AveragePatch(rgba, 240, 24, 0) * 255;
        var floor = AveragePatch(rgba, 240, 240, 0) * 255;
        Console.WriteLine($"ATMOSPHERE_BRIGHTNESS {name} upper_centre={upper} floor={floor}");
        if (time == .5f) Assert.InRange(upper.Z, 150, 210);
        if (time == 0) Assert.InRange(upper.Z, 15, 40);
        if (time == .747f) Assert.True(floor.X > 0 && floor.Y > 0 && floor.Z > 0,
            "The sunset fixture must contain a lit floor receiver above display black.");
        GoldenCompare.AssertOrUpdate(name, rgba, AtmosphereGoldenScene.Width, AtmosphereGoldenScene.Height);
    }
}
