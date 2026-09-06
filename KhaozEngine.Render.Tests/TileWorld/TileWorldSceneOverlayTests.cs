using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWorldSceneOverlayTests
{
    [Fact]
    public void Legacy_scene_refuses_an_overlay_instead_of_drawing_it_opaque()
    {
        ITileWorldScene scene = new LegacyTileWorldScene();

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() =>
            scene.DrawOverlayMesh(new MeshHandle(7, 3), Matrix4x4.Identity));

        Assert.Contains("overlay", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scene3D_adapter_queues_an_overlay_mesh_in_the_overlay_pass()
    {
        var device = new FakeGpuDevice();
        using IGpuTexture targetTexture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            32, 24, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using IGpuFramebuffer target = device.Factory.CreateFramebuffer(null, targetTexture);
        using var scene = new Scene3D(device, target.Outputs);
        var adapter = new Scene3DTileWorldScene(scene);

        scene.Begin();
        adapter.DrawOverlayMesh(new MeshHandle(7, 3), Matrix4x4.CreateTranslation(4f, 5f, 6f));

        Assert.Equal(1, scene.OverlayMeshDrawCount);
    }

    sealed class LegacyTileWorldScene : ITileWorldScene
    {
        public MeshHandle LoadMesh(GltfMesh mesh) => default;
        public void UnloadMesh(MeshHandle handle) { }
        public void DrawMesh(MeshHandle handle, Matrix4x4 world) { }
        public IReadOnlyList<MeshHandle> LoadPropMeshes(IReadOnlyList<GltfMeshPart> parts) =>
            Array.Empty<MeshHandle>();
        public void UnloadPropMeshes(IReadOnlyList<MeshHandle> handles) { }
        public int DrawProps(IReadOnlyList<PropPlacement> placements,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts, Vector3 focus, float drawRadius) => 0;
    }
}
