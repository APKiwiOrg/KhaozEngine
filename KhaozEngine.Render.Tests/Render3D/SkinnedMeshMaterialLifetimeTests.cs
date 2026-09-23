using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SkinnedMeshMaterialLifetimeTests
{
    static readonly byte[] Pixel = { 255, 255, 255, 255 };
    static readonly GpuOutputDescription Outputs = new(null, GpuPixelFormat.R8G8B8A8UNorm);

    [Fact]
    public void Surface_maps_cutoff_is_retained_on_the_live_entry()
    {
        using Harness h = Harness.Create();
        Scene3D.TextureHandle albedo = h.Scene.LoadTexture(Pixel, 1, 1);

        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(
            Tube(), new Scene3D.SurfaceMaps(albedo, alphaCutoff: 0.42f));

        Assert.Equal(0.42f, h.Scene.SkinnedAlphaCutoffAt(mesh));
    }

    [Fact]
    public void Texture_overload_creates_an_outline_material_with_zero_cutoff()
    {
        using Harness h = Harness.Create();
        Scene3D.TextureHandle albedo = h.Scene.LoadTexture(Pixel, 1, 1);
        int setsBefore = h.Factory.ResourceSets.Count;

        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(Tube(), albedo);

        FakeResourceSet[] created = h.Factory.ResourceSets.Skip(setsBefore).ToArray();
        Assert.Equal(3, created.Length);
        Assert.Equal(0f, h.Scene.SkinnedAlphaCutoffAt(mesh));
        Assert.Same(created[2], h.Scene.SkinnedOutlineMaterialSetAt(mesh));
    }

    [Fact]
    public void Surface_maps_with_albedo_create_cpu_gpu_and_outline_materials()
    {
        using Harness h = Harness.Create();
        Scene3D.TextureHandle albedo = h.Scene.LoadTexture(Pixel, 1, 1);
        int setsBefore = h.Factory.ResourceSets.Count;

        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(
            Tube(), new Scene3D.SurfaceMaps(albedo, alphaCutoff: 0.5f));

        FakeResourceSet[] created = h.Factory.ResourceSets.Skip(setsBefore).ToArray();
        Assert.Equal(3, created.Length);
        Assert.Same(created[0], h.Scene.SkinnedCpuMaterialSetAt(mesh));
        Assert.Same(created[1], h.Scene.SkinnedGpuMaterialSetAt(mesh));
        Assert.Same(created[2], h.Scene.SkinnedOutlineMaterialSetAt(mesh));
    }

    [Fact]
    public void Surface_maps_without_albedo_create_no_outline_material()
    {
        using Harness h = Harness.Create();
        Scene3D.TextureHandle normal = h.Scene.LoadTexture(Pixel, 1, 1);
        int setsBefore = h.Factory.ResourceSets.Count;

        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(
            Tube(), new Scene3D.SurfaceMaps(default, normal, alphaCutoff: 0.5f));

        FakeResourceSet[] created = h.Factory.ResourceSets.Skip(setsBefore).ToArray();
        Assert.Equal(2, created.Length);
        Assert.Same(created[0], h.Scene.SkinnedCpuMaterialSetAt(mesh));
        Assert.Same(created[1], h.Scene.SkinnedGpuMaterialSetAt(mesh));
        Assert.Null(h.Scene.SkinnedOutlineMaterialSetAt(mesh));
        Assert.Equal(0.5f, h.Scene.SkinnedAlphaCutoffAt(mesh));
    }

    [Fact]
    public void Gpu_material_failure_disposes_buffers_and_cpu_material_without_a_live_handle()
    {
        using Harness h = Harness.Create();
        Scene3D.TextureHandle albedo = h.Scene.LoadTexture(Pixel, 1, 1);
        int buffersBefore = h.Factory.Buffers.Count;
        int setsBefore = h.Factory.ResourceSets.Count;
        h.Factory.ThrowOnResourceSetCreate = setsBefore + 2;

        Assert.Throws<InvalidOperationException>(() =>
            h.Scene.LoadSkinnedMesh(Tube(), new Scene3D.SurfaceMaps(albedo)));

        Assert.Equal(0, h.Scene.LiveSkinnedMeshCount);
        Assert.Equal(2, h.Factory.Buffers.Count - buffersBefore);
        Assert.All(h.Factory.Buffers.Skip(buffersBefore), buffer => Assert.True(buffer.Disposed));
        Assert.Single(h.Factory.ResourceSets.Skip(setsBefore));
        Assert.All(h.Factory.ResourceSets.Skip(setsBefore), set => Assert.True(set.Disposed));
    }

    [Fact]
    public void Load_is_transactional_through_outline_material_creation()
    {
        using Harness h = Harness.Create();
        Scene3D.TextureHandle albedo = h.Scene.LoadTexture(Pixel, 1, 1);
        int buffersBefore = h.Factory.Buffers.Count;
        int setsBefore = h.Factory.ResourceSets.Count;
        h.Factory.ThrowOnResourceSetCreate = setsBefore + 3;

        Assert.Throws<InvalidOperationException>(() =>
            h.Scene.LoadSkinnedMesh(Tube(), new Scene3D.SurfaceMaps(albedo, alphaCutoff: 0.5f)));

        Assert.Equal(0, h.Scene.LiveSkinnedMeshCount);
        Assert.All(h.Factory.Buffers.Skip(buffersBefore), b => Assert.True(b.Disposed));
        Assert.All(h.Factory.ResourceSets.Skip(setsBefore), s => Assert.True(s.Disposed));

        h.Factory.ThrowOnResourceSetCreate = 0;
        Assert.True(h.Scene.LoadSkinnedMesh(
            Tube(), new Scene3D.SurfaceMaps(albedo, alphaCutoff: 0.5f)).Generation > 0);
    }

    [Fact]
    public void Index_buffer_failure_disposes_the_vertex_buffer_without_a_live_handle()
    {
        using Harness h = Harness.Create();
        int buffersBefore = h.Factory.Buffers.Count;
        h.Factory.ThrowOnBufferCreate = buffersBefore + 2;

        Assert.Throws<InvalidOperationException>(() => h.Scene.LoadSkinnedMesh(Tube()));

        Assert.Equal(0, h.Scene.LiveSkinnedMeshCount);
        FakeBuffer vertexBuffer = Assert.Single(h.Factory.Buffers.Skip(buffersBefore));
        Assert.True(vertexBuffer.Disposed);
    }

    [Fact]
    public void Shadow_layout_replacement_preserves_outline_material_and_cutoff_only()
    {
        using Harness h = Harness.Create();
        Scene3D.TextureHandle albedo = h.Scene.LoadTexture(Pixel, 1, 1);
        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(
            Tube(), new Scene3D.SurfaceMaps(albedo, alphaCutoff: 0.37f));
        FakeResourceSet oldCpu = Assert.IsType<FakeResourceSet>(h.Scene.SkinnedCpuMaterialSetAt(mesh));
        FakeResourceSet oldGpu = Assert.IsType<FakeResourceSet>(h.Scene.SkinnedGpuMaterialSetAt(mesh));
        FakeResourceSet outline = Assert.IsType<FakeResourceSet>(h.Scene.SkinnedOutlineMaterialSetAt(mesh));

        Assert.True(h.Scene.ReplaceShadowLayout(1024, 2));

        FakeResourceSet newCpu = Assert.IsType<FakeResourceSet>(h.Scene.SkinnedCpuMaterialSetAt(mesh));
        FakeResourceSet newGpu = Assert.IsType<FakeResourceSet>(h.Scene.SkinnedGpuMaterialSetAt(mesh));
        Assert.True(oldCpu.Disposed);
        Assert.True(oldGpu.Disposed);
        Assert.NotSame(oldCpu, newCpu);
        Assert.NotSame(oldGpu, newGpu);
        Assert.Same(outline, h.Scene.SkinnedOutlineMaterialSetAt(mesh));
        Assert.False(outline.Disposed);
        Assert.Equal(0.37f, h.Scene.SkinnedAlphaCutoffAt(mesh));
    }

    [Fact]
    public void Scene_disposal_disposes_the_outline_material()
    {
        using Harness h = Harness.Create();
        Scene3D.TextureHandle albedo = h.Scene.LoadTexture(Pixel, 1, 1);
        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(Tube(), albedo);
        FakeResourceSet outline = Assert.IsType<FakeResourceSet>(h.Scene.SkinnedOutlineMaterialSetAt(mesh));

        h.DisposeScene();

        Assert.True(outline.Disposed);
    }

    static SkinnedGltfMesh Tube() =>
        SkinnedMeshBuilder.BuildTube(0.25f, 2f, 4, 6, 3, Axis.Z);

    sealed class Harness : IDisposable
    {
        readonly FakeGpuDevice _device;
        bool _sceneDisposed;

        Harness()
        {
            _device = new FakeGpuDevice();
            Factory = (FakeGpuResourceFactory)_device.Factory;
            Scene = new Scene3D(_device, Outputs);
        }

        internal FakeGpuResourceFactory Factory { get; }
        internal Scene3D Scene { get; }

        internal static Harness Create() => new();

        internal void DisposeScene()
        {
            if (_sceneDisposed) return;
            Scene.Dispose();
            _sceneDisposed = true;
        }

        public void Dispose()
        {
            DisposeScene();
            _device.Dispose();
        }
    }
}
