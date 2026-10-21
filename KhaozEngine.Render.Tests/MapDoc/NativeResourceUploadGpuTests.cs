using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

// Required strict three-backend facts. Task 13 executes these with KE_GPU_TESTS=1, with no capability skips.
public sealed class NativeResourceUploadGpuTests
{
    [GpuFact]
    public void PublishedHandles_DrawRigidMidpointWaterAndFoliageWithoutResourceUpload()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var host = new NativeUploadTestScene(gpu.GpuDevice);
        var scene = host.Scene;
        NativeResourceUploadTests.LoadParts(scene);
        var rigid = Publish(host, new(NativeResourceUploadTests.Triangle(), null, null, default));
        var clip = WaterSurfaceDomain.Encode(new Vector2[] { new(0, 0), new(1, 0), new(0, 1) });
        var water = Publish(host, new(NativeResourceUploadTests.Triangle(), null, null, clip));
        var foliage = Publish(host, new(null, null, NativeRenderAssetFixtures.FoliageSource(), default));
        var access = new SceneResourceAccess(scene);
        scene.Camera.Frame(new(1, .25f, 1), new(6, 5, 7));
        scene.Begin(); var empty = host.Capture();
        scene.Begin();
        long uploadBytes = host.Device.UploadBytes;
        var individual = new RigidInstanceDraw(default, Matrix4x4.CreateTranslation(-1, 0, 0))
        { Dissolve = .5f, DissolveComplement = 0, Tint = new(.8f, .2f, .1f), Motion = MotionKey.From(77) };
        var proxy = individual with { InvertShadowDissolve = true, DissolveComplement = 1 };
        access.DrawResourceAnchored(rigid, individual, default);
        access.DrawResourceAnchored(rigid, proxy, default);
        Assert.Equal(.5f, scene.QueuedInstancesForTests[0].DissolveThreshold);
        Assert.Equal(1, scene.QueuedInstancesForTests[1].DissolveComplement);
        Assert.True(scene.QueuedInstancesForTests[1].InvertShadowDissolve);
        var look = new WaterLook { Opacity = .63f, SwellAmplitude = .12f, WaveSource = WaterWaveSource.Procedural };
        scene.DrawWater(new WaterSurfaceMesh(access.MeshOf(water), default, Matrix4x4.CreateTranslation(0, .1f, 0), look));
        Assert.Equal(1, scene.BoundedWaterCount);
        Assert.Equal(6, GroundCoverRenderer.DrawPrepared(scene, foliage, new(1, 0, 1),
            new() { DrawRadius = 5, DensityRadius = 3, WindStrength = .4f }, default));
        Assert.Equal(uploadBytes, host.Device.UploadBytes);
        byte[] pixels = host.Capture();
        Assert.Equal(uploadBytes, host.Device.UploadBytes);
        Assert.Equal(0, scene.LastFoliageStats.InstanceUploadBytes);
        Assert.True(pixels.Where((b, i) => i % 4 != 3 && b != empty[i]).Count() >= 12);
        scene.ReleaseResource(foliage); scene.ReleaseResource(water); scene.ReleaseResource(rigid);
    }

    [GpuFact]
    public void PublishedFoliage_FocusUniformsChangeWithoutPreparationOrUpload()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var host = new NativeUploadTestScene(gpu.GpuDevice);
        var scene = host.Scene;
        NativeResourceUploadTests.LoadParts(scene);
        var fixture = NativeRenderAssetFixtures.FoliageSource();
        // Task 7 focus fixture. Three rows at x=0,3,6. Focus moves from x=0 to x=5, draw=4 and density=2.
        var source = new SceneFoliageSource(new(32000, -500, -32000),
            Enumerable.Range(0, 3).Select(i => Matrix4x4.CreateTranslation(i * 3, 0, 0)).ToArray(),
            new float[] { .1f, .2f, .3f }, new[] { 0, 1, 0 }, fixture.Models, SceneFoliageTransformSpace.AnchorLocal);
        var handle = Publish(host, new(null, null, source, default));
        scene.RenderOrigin = new(32000, -500, -32000);
        scene.Camera.Frame(new(32003, -499.5f, -32000), new(8, 4, 5));
        var settings = new FoliageRenderSettings
        {
            DrawRadius = 4,
            DensityRadius = 2,
            FadeBandWidth = 1,
            QualityDensity = 1,
            DistantDensity = .35f,
            InstanceFadeBandWidth = .5f,
            WindFadeBladePixels = 2,
            WindStrength = .7f,
            WindDirection = new(.25f, .75f)
        };
        scene.Begin();
        var prepared = scene.ResourceOf(handle).PreparedFoliage;
        long bytes = host.Device.UploadBytes;
        int before = GroundCoverRenderer.DrawPrepared(scene, handle, Vector3.Zero, settings, source.Anchor);
        Assert.Equal(new Vector4(0, 0, 0, 4), Assert.Single(scene.FoliageUniformsForTests.ToArray()).FocusRadius);
        var first = host.Capture();
        scene.Begin();
        settings = settings with { WindStrength = .2f };
        int after = GroundCoverRenderer.DrawPrepared(scene, handle, new(5, 0, 0), settings, source.Anchor);
        var uniforms = Assert.Single(scene.FoliageUniformsForTests.ToArray());
        Assert.Equal(new Vector4(5, 0, 0, 4), uniforms.FocusRadius);
        Assert.Equal(new Vector4(1, .35f, 2, 1), uniforms.Density);
        Assert.Equal(.2f, uniforms.FadeWind.Y);
        var second = host.Capture();
        Assert.Equal(5, before); Assert.Equal(5, after);
        Assert.Equal(0, scene.LastFoliageStats.InstanceUploadBytes);
        Assert.Equal(bytes, host.Device.UploadBytes);
        Assert.Same(prepared, scene.ResourceOf(handle).PreparedFoliage);
        Assert.True(first.Where((b, i) => i % 4 != 3 && b != second[i]).Count() >= 12);
        scene.Begin();
        settings = settings with { QualityDensity = .25f };
        Assert.Equal(3, GroundCoverRenderer.DrawPrepared(scene, handle, new(5, 0, 0), settings, source.Anchor));
        Assert.Equal(.25f, Assert.Single(scene.FoliageUniformsForTests.ToArray()).Density.X);
        var thinned = host.Capture();
        Assert.True(second.Where((b, i) => i % 4 != 3 && b != thinned[i]).Count() >= 3);
        Assert.Equal(bytes, host.Device.UploadBytes);
        Assert.Equal(0, scene.LastFoliageStats.InstanceUploadBytes);
        Assert.Same(prepared, scene.ResourceOf(handle).PreparedFoliage);
        scene.ReleaseResource(handle);
    }
    static SceneResourceHandle Publish(NativeUploadTestScene host, SceneUploadData data)
    {
        using var upload = host.Scene.PrepareResourceUpload(data, new NativeUploadReservation(host.Scene.MeasureResourceUpload(data)));
        upload.Advance(long.MaxValue);
        host.Device.WaitForIdle();
        Assert.True(upload.Ready);
        return upload.Publish();
    }
}
