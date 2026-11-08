using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Key-light visibility in the air of a shadowed valley, KhaozEngine #1411.</summary>
[Collection("NativeDeviceLifecycle")]
public sealed class AtmosphereFogKeyShadowGpuTests : IClassFixture<AtmosphereGoldenScene>
{
    readonly AtmosphereGoldenScene _fixture;

    public AtmosphereFogKeyShadowGpuTests(AtmosphereGoldenScene fixture) => _fixture = fixture;

    [GpuFact]
    public void RidgeShadow_RemovesTheKeyGlowAndKeepsAmbientFog()
    {
        byte[] clear = Capture(ridge: false);
        byte[] blocked = Capture(ridge: true);
        float openFog = PatchLuma(clear), shadowFog = PatchLuma(blocked);
        Console.WriteLine($"FOG_KEY_SHADOW open={openFog} blocked={shadowFog} ratio={shadowFog / openFog}");
        Assert.True(openFog > .05f, $"The open receiver must carry sunset fog, got {openFog}.");
        Assert.True(shadowFog > .005f, $"Ambient fog must remain under the ridge, got {shadowFog}.");
        Assert.True(shadowFog < openFog * .5f,
            $"The ridge must remove most of the key glow, open {openFog}, blocked {shadowFog}.");
    }

    [GpuFact]
    public void UnoccludedFog_IsByteIdenticalWhenDisabledAndAfterSceneReuse()
    {
        Capture(ridge: true);
        byte[] enabled = Capture(ridge: false);
        byte[] disabled = Capture(_fixture, ridge: false, samples: 0);
        Assert.Equal(disabled, enabled);
        using var fresh = new AtmosphereGoldenScene();
        Assert.Equal(Capture(fresh, ridge: false, samples: 8), enabled);
    }

    byte[] Capture(bool ridge) => Capture(_fixture, ridge, 8);

    static byte[] Capture(AtmosphereGoldenScene fixture, bool ridge, int samples)
        => fixture.CaptureEffects(scene => Configure(fixture, scene, samples), scene => Draw(fixture, scene, ridge));

    static void Configure(AtmosphereGoldenScene fixture, Scene3D scene, int samples)
    {
        fixture.TimeOfDay = .732f;
        var sun = -SunCycle.Evaluate(fixture.TimeOfDay, fixture.Cycle).LightDirection;
        var camera = (FlyCamera3D)scene.CameraOverride!;
        camera.Position = new Vector3(0, 2, 0);
        camera.Yaw = MathF.Atan2(sun.X, sun.Z);
        camera.Pitch = -MathF.Atan2(2, 60);
        camera.FarPlane = 2000;
        scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        scene.Post.Quality.Shadows.ShadowNearDistance = 50;
        scene.Post.Quality.Shadows.ShadowMaxDistance = 200;
        scene.Post.Quality.Shadows.ShadowStrength = 1;
        scene.Post.Atmosphere.HazeDensityPerKm = .8f;
        scene.Post.Atmosphere.HazeScaleHeightKm = 100;
        scene.Post.Atmosphere.NightSkyFloor = 0;
        scene.Post.Atmosphere.AerialShadowSamples = samples;
    }

    static void Draw(AtmosphereGoldenScene fixture, Scene3D scene, bool ridge)
    {
        var ground = scene.GetOrLoadMesh("atmosphere-key-shadow-ground", () => scene.LoadMesh(MeshPrimitives.Tile(2000)));
        var box = scene.GetOrLoadMesh("atmosphere-key-shadow-ridge", () => scene.LoadMesh(MeshPrimitives.Box()));
        scene.Draw(ground, Matrix4x4.Identity, Color.Black);
        var sun = -SunCycle.Evaluate(fixture.TimeOfDay, fixture.Cycle).LightDirection;
        var forward = Vector3.Normalize(new Vector3(sun.X, 0, sun.Z));
        var right = new Vector3(forward.Z, 0, -forward.X);
        var centre = forward * 100 + right * (ridge ? 0 : 400) + Vector3.UnitY * 20;
        float yaw = MathF.Atan2(forward.X, forward.Z);
        scene.Draw(box, Matrix4x4.CreateScale(240, 40, 8) * Matrix4x4.CreateRotationY(yaw)
            * Matrix4x4.CreateTranslation(centre), Color.Black);
    }

    static float PatchLuma(byte[] pixels)
    {
        float sum = 0;
        for (int y = AtmosphereGoldenScene.Height / 2 - 1; y <= AtmosphereGoldenScene.Height / 2 + 1; y++)
            for (int x = AtmosphereGoldenScene.Width / 2 - 1; x <= AtmosphereGoldenScene.Width / 2 + 1; x++)
            {
                int i = (y * AtmosphereGoldenScene.Width + x) * 4;
                sum += (.2126f * pixels[i] + .7152f * pixels[i + 1] + .0722f * pixels[i + 2]) / 255;
            }
        return sum / 9;
    }
}
