using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeResourceUploadWaterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrameBoundary_ClearsPublishedWaterForStandaloneAndSharedSubmission(bool shared)
    {
        using var host = new NativeUploadTestScene();
        using var submission = shared ? new GpuFrameSubmission(host.Device) : null;
        var handle = NativeResourceUploadTests.Publish(host.Scene, NativeWaterUploadProof.Input());
        host.Scene.Begin();
        host.Scene.DrawWater(NativeWaterUploadProof.Surface(new SceneResourceAccess(host.Scene).MeshOf(handle)));
        Assert.Equal(1, host.Scene.BoundedWaterCount);

        if (submission is null) host.Scene.Begin();
        else { submission.Begin(); host.Scene.BeginFrame(submission); }

        Assert.Equal(0, host.Scene.BoundedWaterCount);
        host.Scene.DrawWater(NativeWaterUploadProof.Surface(new SceneResourceAccess(host.Scene).MeshOf(handle)));
        Assert.Equal(1, host.Scene.BoundedWaterCount);
        host.Scene.ReleaseResource(handle);
    }

    [Fact]
    public void PublishedWater_QueuedLookAndBoundDomainMatchSubmittedValues()
    {
        using var host = new NativeUploadTestScene();
        var handle = NativeResourceUploadTests.Publish(host.Scene, NativeWaterUploadProof.Input());
        host.Scene.Begin();
        long bytes = host.Device.UploadBytes;
        host.Scene.DrawWater(NativeWaterUploadProof.Surface(new SceneResourceAccess(host.Scene).MeshOf(handle)));
        NativeWaterUploadProof.AssertSubmitted(host, handle);
        Assert.Equal(bytes, host.Device.UploadBytes);
        host.Scene.ReleaseResource(handle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublishedWater_RepeatedFramesDoNotUploadResourceData(bool viewportPrepared)
    {
        using var host = new NativeUploadTestScene();
        var scene = host.Scene;
        var handle = NativeResourceUploadTests.Publish(scene, NativeWaterUploadProof.Input());
        var surface = NativeWaterUploadProof.Surface(new SceneResourceAccess(scene).MeshOf(handle));
        long bytes = host.Device.UploadBytes;
        int textureWrites = host.Device.TextureWrites;

        DrawFrame();
        long firstBytes = host.Device.UploadBytes;
        int firstTextureWrites = host.Device.TextureWrites;
        DrawFrame();
        Assert.Equal((textureWrites, textureWrites), (firstTextureWrites, host.Device.TextureWrites));
        Assert.Equal((bytes, bytes), (firstBytes, host.Device.UploadBytes));
        scene.ReleaseResource(handle);

        void DrawFrame()
        {
            scene.Begin();
            scene.DrawWater(surface);
            using var commands = host.Device.Factory.CreateCommandList();
            if (viewportPrepared) scene.PrepareFrame(64, 48);
            else scene.PrepareFrame();
            host.Record(commands);
            host.Device.Submit(commands);
        }
    }
}

internal static class NativeWaterUploadProof
{
    internal static SceneUploadData Input() => new(Mesh(), null, null,
        WaterSurfaceDomain.Encode(new Vector2[] { new(-2, -2), new(2, -2), new(-2, 2) }));

    internal static GltfMesh Mesh() => new(new ModelVertex[]
    {
        new(new(-2, 0, -2), Vector3.UnitY, Vector4.One),
        new(new(2, 0, -2), Vector3.UnitY, Vector4.One),
        new(new(-2, 0, 2), Vector3.UnitY, Vector4.One)
    }, new uint[] { 0, 2, 1 });

    internal static WaterSurfaceMesh Surface(MeshHandle mesh) => new(mesh, default,
        Matrix4x4.CreateTranslation(0, .1f, 0), new WaterLook
        {
            Opacity = .63f,
            SwellAmplitude = .12f,
            SwellWavelength = 4,
            WaveSource = WaterWaveSource.Procedural,
            DeepColor = new(.08f, .25f, .45f, 1)
        });

    internal static void AssertSubmitted(NativeUploadTestScene host, SceneResourceHandle handle)
    {
        var scene = host.Scene;
        var actual = Assert.Single(scene.BoundedWaterForTests);
        Assert.Equal(new SceneResourceAccess(scene).MeshOf(handle), actual.Mesh);
        Assert.Equal(default, actual.Anchor);
        Assert.Equal(Matrix4x4.CreateTranslation(0, .1f, 0), actual.LocalTransform);
        Assert.Equal(.63f, actual.Opacity);
        Assert.Equal(.12f, actual.SwellAmplitude);
        Assert.Equal(4, actual.SwellWavelength);
        Assert.Equal(WaterWaveSource.Procedural, actual.WaveSource);
        Assert.Equal(new Color(.08f, .25f, .45f, 1), actual.DeepColor);
        object identity = scene.BoundedWaterBufferIdentityForTests(actual.Mesh);
        var buffer = Assert.Single(host.Device.BufferCopies.Keys, candidate => Scene3D.BufferMatchesIdentityForTests(candidate, identity));
        var floats = MemoryMarshal.Cast<byte, float>(host.Device.BufferCopies[buffer]);
        Assert.Equal(new float[] { 1, 1, 0, 0, -2, -2, 0, 0, 2, -2, 0, 0, -2, 2, 0, 0 }, floats.ToArray());
    }
}
