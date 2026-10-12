using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

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

    byte[] MoonCapture(bool mirrored) => _fixture.Capture(scene =>
    {
        _fixture.TimeOfDay = .4f;
        _fixture.Cycle.MoonHourOffset = 6;
        _fixture.Cycle.MoonDeclinationDegrees = 20;
        _fixture.Cycle.HeadingDegrees = 35;
        var state = SunCycle.Evaluate(.4f, _fixture.Cycle);
        var actual = -state.MoonDirection;
        var oppositeSun = state.LightDirection;
        Aim((FlyCamera3D)scene.CameraOverride!, mirrored ? oppositeSun : actual);
    });

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
        => GoldenCompare.AssertOrUpdate(name, _fixture.Capture(time), AtmosphereGoldenScene.Width, AtmosphereGoldenScene.Height);
}
