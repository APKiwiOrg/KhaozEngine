using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Render3DSnapshot owns its image size, so it prepares bound water through the viewport path itself.
/// The runner creates its own device, so this needs a real one.</summary>
public sealed class Render3DSnapshotWaterPrepareGpuTests
{
    const int Size = 96;

    [GpuFact]
    public void BoundWaterCapturesWithoutCallbackPreparationAndMatchesSelfPreparation()
    {
        byte[] hosted = Capture(water: true, prepareInCallback: false);
        byte[] manual = Capture(water: true, prepareInCallback: true);
        byte[] dry = Capture(water: false, prepareInCallback: false);
        Assert.Equal(hosted, manual);
        Assert.NotEqual(dry, hosted);
    }

    static byte[] Capture(bool water, bool prepareInCallback)
    {
        MeshHandle bed = default;
        WaterPlane plane = default;
        return Render3DSnapshot.Capture(Size, Size, scene =>
        {
            scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
            scene.Post.Starfield = false;
            scene.Post.Sky.Enabled = false;
            scene.Post.Background = BackgroundMode.Solid;
            scene.Post.BackgroundColor = new Color(0.04f, 0.05f, 0.08f, 1f);
            scene.Post.Water.SwellAmplitude = 0f;
            scene.Camera.Frame(Vector3.Zero, new Vector3(8f, 2f, 8f));
            bed = scene.LoadMesh(MeshPrimitives.Box(1f));
            WaterOpticsHandle handle = scene.LoadWaterOpticsField(new WaterOpticsField(2, 2, [2f, 2f, 2f, 2f]));
            plane = new WaterPlane(0f, 0f, 0f, 5f,
                optics: new WaterOpticsBinding(handle, new Vector3(-5f, 0f, -5f), new Vector2(10f)));
        }, scene =>
        {
            scene.EffectTimeSeconds = 0f;
            scene.Draw(bed, Matrix4x4.CreateScale(8f, 1f, 8f) * Matrix4x4.CreateTranslation(0f, -1.5f, 0f),
                new Color(0.6f, 0.5f, 0.3f, 1f));
            if (water) scene.DrawWater(plane);
            if (prepareInCallback) scene.PrepareFrame(Size, Size);
        }, frames: 2);
    }
}
