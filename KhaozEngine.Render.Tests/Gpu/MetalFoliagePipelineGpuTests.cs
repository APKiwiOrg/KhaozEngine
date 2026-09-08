using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class MetalFoliagePipelineGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void TheRealFoliagePipelineDrawsWithTheShippedInstanceLayout()
    {
        if (!MetalDormancy.NativeDeviceAvailable(output)) return;

        using IGpuDevice device = new MetalBackendProvider().CreateHeadless().Device;
        using var preview = new Render3DPreview(device, 240, 160);
        Scene3D scene = preview.Scene;
        GltfMesh mesh = MeshPrimitives.Tile(.2f, 2f);
        foreach (ref ModelVertex vertex in mesh.Vertices.AsSpan())
            vertex.Color = new Vector4(.3f, .7f, .15f, 1f);

        MeshHandle blade = scene.LoadMesh(mesh);
        using FoliageBatch batch = scene.CreateFoliageBatch(
            [new FoliageInstance(blade, Matrix4x4.Identity, .2f)]);
        scene.Post.TransparentBackground = false;
        scene.Post.Starfield = false;
        scene.Post.Quality.AntiAliasing = default;
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        scene.Camera.Frame(Vector3.Zero, new Vector3(4f, 3f, 4f));

        preview.Capture(s => s.DrawFoliage(batch, Vector3.Zero,
            new FoliageRenderSettings { DrawRadius = 100f, DistantDensity = 1f }));
        _ = preview.ReadbackRgba();

        Assert.Equal(1, scene.LastFoliageStats.CandidateInstances);
        Assert.Equal(1, scene.LastFoliageStats.SubmittedPatches);
    }
}
