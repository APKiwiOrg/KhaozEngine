using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class IndirectDiffuseGoldenTests : IClassFixture<AtmosphereGoldenScene>
{
    const string DirectionalScene = "scene3d_indirect_directional";
    readonly AtmosphereGoldenScene _fixture;
    MeshHandle _box;

    public IndirectDiffuseGoldenTests(AtmosphereGoldenScene fixture) => _fixture = fixture;

    [GpuFact]
    public void Golden_IndirectDirectionalNoon()
    {
        byte[] rgba = Capture(IndirectDiffuseMode.Directional);
        GoldenCompare.AssertOrUpdate(DirectionalScene, rgba, AtmosphereGoldenScene.Width, AtmosphereGoldenScene.Height);
    }

    [GpuFact]
    public void TheUndersideIsLitByTheGround()
    {
        byte[] uniform = Capture(IndirectDiffuseMode.Uniform);
        byte[] directional = Capture(IndirectDiffuseMode.Directional);
        Vector3 uniformPixel = UndersideCentre(uniform);
        Vector3 directionalPixel = UndersideCentre(directional);
        float uniformLuminance = Luminance(uniformPixel);
        float directionalLuminance = Luminance(directionalPixel);
        Console.WriteLine($"INDIRECT_UNDERSIDE uniform_rgb={uniformPixel} directional_rgb={directionalPixel} "
            + $"uniform_luminance={uniformLuminance:F6} directional_luminance={directionalLuminance:F6}");
        Assert.True(directionalLuminance >= 2 * uniformLuminance && directionalLuminance > 0,
            $"The underside centre must be visibly lit by ground bounce, at least twice Uniform luminance. "
            + $"Uniform RGB={uniformPixel}, luminance={uniformLuminance:F6}. "
            + $"Directional RGB={directionalPixel}, luminance={directionalLuminance:F6}.");
    }

    byte[] Capture(IndirectDiffuseMode mode)
    {
        if (_box.Generation == 0) _box = _fixture.Scene.LoadMesh(MeshPrimitives.Box());
        byte[] rgba = _fixture.CaptureEffects(scene =>
        {
            _fixture.TimeOfDay = .5f;
            scene.Post.Atmosphere.Enabled = true;
            scene.Post.IndirectDiffuse = mode;
            var camera = (FlyCamera3D)scene.CameraOverride!;
            camera.Position = new Vector3(0, 1.5f, 2.9f);
            camera.Yaw = MathF.PI;
            camera.Pitch = 35 * MathF.PI / 180;
        }, scene =>
        {
            scene.Draw(_box, Matrix4x4.CreateScale(24, .1f, 24)
                * Matrix4x4.CreateTranslation(0, -.05f, 0), Color.White);
            scene.Draw(_box, Matrix4x4.CreateScale(6, .3f, 6)
                * Matrix4x4.CreateTranslation(0, 3.15f, 0), Color.White);
            foreach (float x in new[] { -2.8f, 2.8f })
                foreach (float z in new[] { -2.8f, 2.8f })
                    scene.Draw(_box, Matrix4x4.CreateScale(.15f, 3, .15f)
                        * Matrix4x4.CreateTranslation(x, 1.5f, z), Color.White);
        });
        AtmosphereGoldenScene.WriteCapture(mode == IndirectDiffuseMode.Directional
            ? DirectionalScene : "scene3d_indirect_uniform", rgba);
        return rgba;
    }

    static Vector3 UndersideCentre(byte[] rgba)
    {
        int i = (AtmosphereGoldenScene.Height / 2 * AtmosphereGoldenScene.Width + AtmosphereGoldenScene.Width / 2) * 4;
        return new Vector3(rgba[i], rgba[i + 1], rgba[i + 2]);
    }

    static float Luminance(Vector3 rgb) => Vector3.Dot(rgb / 255, new Vector3(.2126f, .7152f, .0722f));
}
