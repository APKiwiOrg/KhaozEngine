using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Locks the compatibility fallback and shipped Scene3D forwarding for the tile-world rigid dissolve
/// seam from issue #827.</summary>
public sealed class TileWorldSceneDissolveTests
{
    [Fact]
    public void Legacy_scene_uses_solid_draw_for_dissolve_request()
    {
        var legacy = new LegacyTileWorldScene();
        ITileWorldScene scene = legacy;
        var handle = new MeshHandle(7, 3);
        Matrix4x4 world = Matrix4x4.CreateTranslation(4f, 5f, 6f);

        scene.DrawMeshDissolved(handle, world, 0.6f, 0.12f, new Color(1f, 0.4f, 0.1f, 1f));

        Assert.Equal(handle.Index, legacy.Drawn.Handle.Index);
        Assert.Equal(handle.Generation, legacy.Drawn.Handle.Generation);
        Assert.Equal(world, legacy.Drawn.World);
    }

    [Fact]
    public void Scene3D_adapter_forwards_rigid_dissolve_parameters()
    {
        var device = new FakeGpuDevice();
        IGpuResourceFactory factory = device.Factory;
        using IGpuTexture targetTexture = factory.CreateTexture(GpuTextureDescription.Texture2D(
            32, 24, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using IGpuFramebuffer target = factory.CreateFramebuffer(null, targetTexture);
        using var scene = new Scene3D(device, target.Outputs);
        scene.Post.Starfield = false;
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        MeshHandle handle = scene.LoadMesh(MeshPrimitives.Box());
        var adapter = new Scene3DTileWorldScene(scene);
        Matrix4x4 world = Matrix4x4.CreateTranslation(4f, 5f, 6f);
        var edge = new Color(0.9f, 0.3f, 0.1f, 1f);
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };

        scene.Begin();
        adapter.DrawMeshDissolved(handle, world, dissolve: 0.65f, edgeWidth: 0.14f, edgeColor: edge);
        scene.PrepareFrame();
        scene.RenderInternal(commands, 32, 24, target);

        RecordingGpuCommandList.Upload upload = Assert.Single(commands.Uploads,
            x => x.Bytes == ModelRenderer.InstanceData.SizeInBytes);
        ModelRenderer.InstanceData packed = MemoryMarshal.Read<ModelRenderer.InstanceData>(upload.Data!);
        Assert.Equal(world, packed.Model);
        Assert.Equal((Vector4)Color.White, packed.Tint);
        Assert.Equal((Vector4)edge, packed.Emissive);
        Assert.Equal(new Vector4(0f, 32f, 0f, 0f), packed.SpecParams);
        Assert.Equal(0.65f, packed.Dissolve.X, 5);
        Assert.Equal(0.14f, packed.Dissolve.Y, 5);
    }

    sealed class LegacyTileWorldScene : ITileWorldScene
    {
        public (MeshHandle Handle, Matrix4x4 World) Drawn { get; private set; }

        public MeshHandle LoadMesh(GltfMesh mesh) => default;
        public void UnloadMesh(MeshHandle handle) { }
        public void DrawMesh(MeshHandle handle, Matrix4x4 world) => Drawn = (handle, world);
        public IReadOnlyList<MeshHandle> LoadPropMeshes(IReadOnlyList<GltfMeshPart> parts) =>
            Array.Empty<MeshHandle>();
        public void UnloadPropMeshes(IReadOnlyList<MeshHandle> handles) { }
        public int DrawProps(IReadOnlyList<PropPlacement> placements,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts, Vector3 focus, float drawRadius) => 0;
    }
}
