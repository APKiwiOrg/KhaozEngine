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

/// <summary>Locks the compatibility refusal and shipped Scene3D forwarding for the tile-world skinned seam.</summary>
public sealed class TileWorldSceneSkinnedTests
{
    [Fact]
    public void Legacy_scene_refuses_every_skinned_operation()
    {
        ITileWorldScene scene = new LegacyTileWorldScene();
        SkinnedGltfMesh mesh = SkinnedMeshBuilder.BuildTube(0.25f, 1f, 4, 3, 2, Axis.Z);
        var handle = new SkinnedMeshHandle(7, 3);
        var maps = new GltfMaterialMaps(null, null, null);

        AssertSkinnedNotSupported(() => scene.LoadSkinnedMesh(mesh));
        AssertSkinnedNotSupported(() => scene.LoadSkinnedMesh(mesh, maps));
        AssertSkinnedNotSupported(() => scene.UnloadSkinnedMesh(handle));
        AssertSkinnedNotSupported(() =>
            scene.DrawSkinned(handle, mesh.RestPose, Matrix4x4.Identity, Color.White));
        AssertSkinnedNotSupported(() =>
            scene.DrawSkinnedDissolved(handle, mesh.RestPose, Matrix4x4.Identity, Color.White,
                dissolve: 0.5f, edgeWidth: 0.1f, edgeColor: Color.White));
    }

    [Fact]
    public void Scene3D_adapter_forwards_skinned_load_draw_and_unload()
    {
        using var h = new SceneHarness();
        SkinnedGltfMesh mesh = SkinnedMeshBuilder.BuildTube(0.25f, 1f, 4, 3, 2, Axis.Z);
        Matrix4x4 world = Matrix4x4.CreateTranslation(0.4f, 0.1f, 0.2f);
        var tint = new Color(0.2f, 0.4f, 0.7f, 0.9f);

        SkinnedMeshHandle handle = h.Adapter.LoadSkinnedMesh(mesh);
        Assert.NotEqual(0, handle.Generation);

        h.Scene.Begin();
        h.Adapter.DrawSkinned(handle, mesh.RestPose, world, tint);

        ModelRenderer.InstanceData packed = h.RenderSingleSkinnedInstance();
        Assert.Equal(world, packed.Model);
        Assert.Equal((Vector4)tint, packed.Tint);
        Assert.Equal(Vector2.Zero, packed.Dissolve);

        h.Scene.Begin();
        h.Adapter.UnloadSkinnedMesh(handle);
        h.Adapter.DrawSkinned(handle, mesh.RestPose, world, tint);
        Assert.Equal(0, h.Scene.SkinnedInstanceCount);
    }

    [Fact]
    public void Scene3D_adapter_forwards_skinned_material_map_load()
    {
        using var h = new SceneHarness();
        SkinnedGltfMesh mesh = SkinnedMeshBuilder.BuildTube(0.25f, 1f, 4, 3, 2, Axis.Z);
        int before = h.Factory.Textures.Count;
        var maps = new GltfMaterialMaps(
            new DecodedImage(new byte[] { 21, 43, 65, 255 }, 1, 1), null, null);

        SkinnedMeshHandle handle = h.Adapter.LoadSkinnedMesh(mesh, maps);

        Assert.NotEqual(0, handle.Generation);
        Assert.Equal(before + 1, h.Factory.Textures.Count);
    }

    [Fact]
    public void Scene3D_adapter_forwards_skinned_dissolve_parameters()
    {
        using var h = new SceneHarness();
        SkinnedGltfMesh mesh = SkinnedMeshBuilder.BuildTube(0.25f, 1f, 4, 3, 2, Axis.Z);
        SkinnedMeshHandle handle = h.Adapter.LoadSkinnedMesh(mesh);
        Matrix4x4 world = Matrix4x4.CreateTranslation(0.4f, 0.1f, 0.2f);
        var tint = new Color(0.2f, 0.4f, 0.7f, 0.9f);
        var edge = new Color(0.9f, 0.3f, 0.1f, 1f);

        h.Scene.Begin();
        h.Adapter.DrawSkinnedDissolved(handle, mesh.RestPose, world, tint,
            dissolve: 0.65f, edgeWidth: 0.14f, edgeColor: edge);

        ModelRenderer.InstanceData packed = h.RenderSingleSkinnedInstance();
        Assert.Equal(world, packed.Model);
        Assert.Equal((Vector4)tint, packed.Tint);
        Assert.Equal((Vector4)edge, packed.Emissive);
        Assert.Equal(0.65f, packed.SpecParams.Z, 5);
        Assert.Equal(0.14f, packed.SpecParams.W, 5);
    }

    static void AssertSkinnedNotSupported(Action action)
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(action);
        Assert.Contains("skinned", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    sealed class SceneHarness : IDisposable
    {
        readonly IGpuTexture _targetTexture;
        readonly IGpuFramebuffer _target;

        public FakeGpuResourceFactory Factory { get; }
        public Scene3D Scene { get; }
        public Scene3DTileWorldScene Adapter { get; }

        public SceneHarness()
        {
            var device = new FakeGpuDevice();
            Factory = (FakeGpuResourceFactory)device.Factory;
            _targetTexture = Factory.CreateTexture(GpuTextureDescription.Texture2D(
                32, 24, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _target = Factory.CreateFramebuffer(null, _targetTexture);
            Scene = new Scene3D(device, _target.Outputs);
            Scene.Post.Starfield = false;
            Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
            Scene.Camera.Frame(Vector3.Zero, new Vector3(4f, 3f, 4f));
            Adapter = new Scene3DTileWorldScene(Scene);
        }

        public ModelRenderer.InstanceData RenderSingleSkinnedInstance()
        {
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
            Scene.PrepareFrame();
            Scene.RenderInternal(commands, 32, 24, _target);

            RecordingGpuCommandList.Upload upload = Assert.Single(commands.Uploads,
                x => x.Bytes == ModelRenderer.InstanceData.SizeInBytes);
            return MemoryMarshal.Read<ModelRenderer.InstanceData>(upload.Data!);
        }

        public void Dispose()
        {
            Scene.Dispose();
            _target.Dispose();
            _targetTexture.Dispose();
        }
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
