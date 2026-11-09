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
    public void PublishedHandles_DrawRigidMidpointAndFoliageWithoutResourceUpload()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var host = new NativeUploadTestScene(gpu.GpuDevice);
        var scene = host.Scene;
        NativeResourceUploadTests.LoadParts(scene);
        var rigid = Publish(host, new(NativeResourceUploadTests.Triangle(), null, null, default));
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
        Assert.Equal(6, GroundCoverRenderer.DrawPrepared(scene, foliage, new(1, 0, 1),
            new() { DrawRadius = 5, DensityRadius = 3, WindStrength = .4f }, default));
        Assert.Equal(uploadBytes, host.Device.UploadBytes);
        byte[] pixels = host.Capture();
        Assert.Equal(uploadBytes, host.Device.UploadBytes);
        Assert.Equal(0, scene.LastFoliageStats.InstanceUploadBytes);
        Assert.True(pixels.Where((b, i) => i % 4 != 3 && b != empty[i]).Count() >= 12);
        scene.ReleaseResource(foliage); scene.ReleaseResource(rigid);
    }

    [GpuFact]
    public void PublishedWater_OnlyWaterChangesPixelsAndPreservesLookAndDomain()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var host = new NativeUploadTestScene(gpu.GpuDevice);
        var scene = host.Scene;
        var bed = Publish(host, new(NativeWaterUploadProof.Mesh(), null, null, default));
        var water = Publish(host, NativeWaterUploadProof.Input());
        var access = new SceneResourceAccess(scene);
        scene.Camera.AspectRatio = 64f / 48f;
        scene.Camera.Frame(Vector3.Zero, new(6, 4, 6));
        scene.EffectTimeSeconds = 12.5f;
        var bedDraw = new RigidInstanceDraw(default, Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(0, -.2f, 0));
        long bytes = host.Device.UploadBytes;
        scene.Begin();
        access.DrawResourceAnchored(bed, bedDraw, default);
        byte[] withoutWater = host.Capture();
        scene.Begin();
        access.DrawResourceAnchored(bed, bedDraw, default);
        scene.DrawWater(NativeWaterUploadProof.Surface(access.MeshOf(water)));
        NativeWaterUploadProof.AssertSubmitted(host, water);
        byte[] withWater = host.Capture();
        // Bounded water is the only changed submission. Omitting its GPU draw makes these images identical.
        Assert.True(ChangedPixels(withoutWater, withWater) >= 24);
        Assert.Equal(bytes, host.Device.UploadBytes);
        scene.ReleaseResource(water); scene.ReleaseResource(bed);
    }

    [GpuFact]
    public void PublishedFoliage_FocusUniformsChangeWithoutPreparationOrUpload()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var host = new NativeUploadTestScene(gpu.GpuDevice);
        var scene = host.Scene;
        NativeFoliageUploadProof.LoadGreenParts(scene);
        // Task 7 focus fixture. Three rows at x=0,3,6. Focus moves from x=0 to x=5, draw=4 and density=2.
        var source = NativeFoliageUploadProof.Source();
        var handle = Publish(host, new(null, null, source, default));
        NativeFoliageUploadProof.ConfigureCamera(scene);
        var settings = NativeFoliageUploadProof.Settings;
        scene.Begin();
        var prepared = scene.ResourceOf(handle).PreparedFoliage;
        long bytes = host.Device.UploadBytes;
        int before = GroundCoverRenderer.DrawPrepared(scene, handle, Vector3.Zero, settings, source.Anchor);
        var firstUniforms = Assert.Single(scene.FoliageUniformsForTests.ToArray());
        Assert.Equal(new Vector4(0, 0, 0, 4), firstUniforms.FocusRadius);
        Assert.Equal(0, firstUniforms.FadeWind.Y);
        Assert.Equal(12.5f, firstUniforms.WindTime.Z);
        var first = host.Capture();
        scene.Begin();
        int after = GroundCoverRenderer.DrawPrepared(scene, handle, new(5, 0, 0), settings, source.Anchor);
        var uniforms = Assert.Single(scene.FoliageUniformsForTests.ToArray());
        Assert.Equal(new Vector4(5, 0, 0, 4), uniforms.FocusRadius);
        Assert.Equal(new Vector4(1, .35f, 2, 1), uniforms.Density);
        Assert.Equal(firstUniforms.FadeWind, uniforms.FadeWind);
        Assert.Equal(firstUniforms.WindTime, uniforms.WindTime);
        var second = host.Capture();
        Assert.Equal(5, before); Assert.Equal(5, after);
        Assert.Equal(0, scene.LastFoliageStats.InstanceUploadBytes);
        Assert.Equal(bytes, host.Device.UploadBytes);
        Assert.Same(prepared, scene.ResourceOf(handle).PreparedFoliage);
        // At focus 0 the x=0 row survives and x=6 is outside radius 4. Focus 5 reverses them.
        Assert.True(NativeFoliageUploadProof.GreenPixels(first, 8, 20) >= 6);
        Assert.Equal(0, NativeFoliageUploadProof.GreenPixels(first, 46, 58));
        Assert.Equal(0, NativeFoliageUploadProof.GreenPixels(second, 8, 20));
        Assert.True(NativeFoliageUploadProof.GreenPixels(second, 46, 58) >= 6);
        scene.Begin();
        settings = settings with { QualityDensity = .25f };
        Assert.Equal(3, GroundCoverRenderer.DrawPrepared(scene, handle, new(5, 0, 0), settings, source.Anchor));
        Assert.Equal(.25f, Assert.Single(scene.FoliageUniformsForTests.ToArray()).Density.X);
        var thinned = host.Capture();
        Assert.Equal(0, NativeFoliageUploadProof.GreenPixels(thinned, 46, 58));
        Assert.Equal(bytes, host.Device.UploadBytes);
        Assert.Equal(0, scene.LastFoliageStats.InstanceUploadBytes);
        Assert.Same(prepared, scene.ResourceOf(handle).PreparedFoliage);
        scene.ReleaseResource(handle);
    }

    [GpuFact]
    public void PublishedFoliage_WindChangesWithoutFocusOrTimeChanges()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var host = new NativeUploadTestScene(gpu.GpuDevice);
        var scene = host.Scene;
        NativeFoliageUploadProof.LoadGreenParts(scene);
        var source = NativeFoliageUploadProof.Source();
        var handle = Publish(host, new(null, null, source, default));
        NativeFoliageUploadProof.ConfigureCamera(scene);
        scene.Camera.Target = new(32005, -499.5f, -32000);
        scene.Camera.OrthoSize = 3;
        var settings = NativeFoliageUploadProof.Settings;
        var prepared = scene.ResourceOf(handle).PreparedFoliage;
        long bytes = host.Device.UploadBytes;
        scene.Begin();
        Assert.Equal(5, GroundCoverRenderer.DrawPrepared(scene, handle, new(5, 0, 0), settings, source.Anchor));
        var calm = Assert.Single(scene.FoliageUniformsForTests.ToArray());
        byte[] first = host.Capture();
        scene.Begin();
        Assert.Equal(5, GroundCoverRenderer.DrawPrepared(scene, handle, new(5, 0, 0), settings with { WindStrength = 1 }, source.Anchor));
        var windy = Assert.Single(scene.FoliageUniformsForTests.ToArray());
        Assert.Equal(calm.FocusRadius, windy.FocusRadius);
        Assert.Equal(new Vector4(5, 0, 0, 4), windy.FocusRadius);
        Assert.Equal(calm.WindTime, windy.WindTime);
        Assert.Equal(12.5f, windy.WindTime.Z);
        Assert.Equal(new Vector4(.5f, 0, 0, 0), calm.FadeWind);
        Assert.Equal(new Vector4(.5f, 1, 0, 0), windy.FadeWind);
        byte[] second = host.Capture();
        Assert.True(ChangedPixels(first, second) >= 3);
        Assert.Equal(bytes, host.Device.UploadBytes);
        Assert.Equal(0, scene.LastFoliageStats.InstanceUploadBytes);
        Assert.Same(prepared, scene.ResourceOf(handle).PreparedFoliage);
        scene.ReleaseResource(handle);
    }

    static int ChangedPixels(byte[] first, byte[] second)
    {
        int count = 0;
        for (int i = 0; i < first.Length; i += 4)
            if (first[i] != second[i] || first[i + 1] != second[i + 1] || first[i + 2] != second[i + 2]) count++;
        return count;
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
