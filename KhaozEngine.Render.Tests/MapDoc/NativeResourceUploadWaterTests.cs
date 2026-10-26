using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
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
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var queue = (IReadOnlyList<WaterSurfaceMesh>)typeof(Scene3D).GetField("_boundedWater", flags)!.GetValue(scene)!;
        var actual = Assert.Single(queue);
        Assert.Equal(new SceneResourceAccess(scene).MeshOf(handle), actual.Mesh);
        Assert.Equal(default, actual.Anchor);
        Assert.Equal(Matrix4x4.CreateTranslation(0, .1f, 0), actual.LocalTransform);
        Assert.Equal(.63f, actual.Look.Opacity);
        Assert.Equal(.12f, actual.Look.SwellAmplitude);
        Assert.Equal(4, actual.Look.SwellWavelength);
        Assert.Equal(WaterWaveSource.Procedural, actual.Look.WaveSource);
        Assert.Equal(new Color(.08f, .25f, .45f, 1), actual.Look.DeepColor);
        var water = typeof(Scene3D).GetField("_water", flags)!.GetValue(scene)!;
        var domains = (IDictionary)water.GetType().GetField("_boundedDomains", flags)!.GetValue(water)!;
        var domain = domains[actual.Mesh]!;
        var buffer = (IGpuBuffer)domain.GetType().GetField("Buffer", flags)!.GetValue(domain)!;
        var floats = MemoryMarshal.Cast<byte, float>(host.Device.BufferCopies[buffer]);
        Assert.Equal(new float[] { 1, 1, 0, 0, -2, -2, 0, 0, 2, -2, 0, 0, -2, 2, 0, 0 }, floats.ToArray());
    }
}
