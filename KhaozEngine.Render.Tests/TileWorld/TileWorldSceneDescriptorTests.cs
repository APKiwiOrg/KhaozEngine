using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The tile-world scene seam's descriptor members (TEMPORAL-FOUNDATIONS-DESIGN section 3). The shipped
/// adapter forwards the whole descriptor, motion key included. An older scene falls back to the draws it already
/// had.</summary>
public sealed class TileWorldSceneDescriptorTests
{
    static readonly MeshHandle Handle = new(7, 3);
    static readonly Matrix4x4 World = Matrix4x4.CreateTranslation(4f, 5f, 6f);
    static readonly MotionKey Body = MotionKey.Combine(MotionKey.From(4242), 3);

    [Fact]
    public void The_adapter_forwards_the_whole_rigid_descriptor_key_included()
    {
        using var harness = new MotionTestScene();
        var adapter = new Scene3DTileWorldScene(harness.Scene);
        var tint = new Color(0.5f, 0.6f, 0.7f, 1f);

        harness.Scene.Begin();
        adapter.DrawMesh(new RigidInstanceDraw(Handle, World) { Tint = tint, CastsShadows = false, Motion = Body });

        SceneInstances.Instance queued = Assert.Single(harness.Scene.QueuedInstancesForTests);
        Assert.Equal(Body, queued.Motion);
        Assert.Equal(tint, queued.Tint);
        Assert.False(queued.CastsShadows);
        Assert.Equal(World, queued.World);
    }

    [Fact]
    public void The_adapter_forwards_the_whole_skinned_descriptor_key_included()
    {
        using var harness = new MotionTestScene();
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        var adapter = new Scene3DTileWorldScene(harness.Scene);

        harness.Scene.Begin();
        adapter.DrawSkinned(new SkinnedInstanceDraw(tube, World) { Dissolve = 0.3f, Motion = Body }, mesh.RestPose);

        SkinnedSceneInstances.Instance queued = Assert.Single(harness.Scene.QueuedSkinnedInstancesForTests);
        Assert.Equal(Body, queued.Motion);
        Assert.Equal(0.3f, queued.DissolveThreshold);
        Assert.Equal(World, queued.World);
    }

    [Fact]
    public void A_legacy_scene_falls_back_to_the_mesh_draws_and_skips_shadow_only()
    {
        var legacy = new LegacyTileWorldScene();
        ITileWorldScene scene = legacy;

        scene.DrawMesh(new RigidInstanceDraw(Handle, World) { Motion = Body });
        scene.DrawMesh(new RigidInstanceDraw(Handle, World) { Dissolve = 0.4f, Motion = Body });   // DrawMeshDissolved's default is solid
        scene.DrawMesh(new RigidInstanceDraw(Handle, World) { ShadowOnly = true });                  // never seen, so nothing

        Assert.Equal(2, legacy.Drawn.Count);
        Assert.All(legacy.Drawn, d => Assert.Equal(World, d.World));
    }

    [Fact]
    public void A_legacy_skinned_scene_falls_back_to_the_plain_skinned_draw()
    {
        var legacy = new LegacyTileWorldScene();
        ITileWorldScene scene = legacy;
        var tint = new Color(0.2f, 0.3f, 0.4f, 1f);
        Matrix4x4[] bones = { Matrix4x4.Identity, Matrix4x4.Identity };

        scene.DrawSkinned(new SkinnedInstanceDraw(new SkinnedMeshHandle(2, 1), World) { Tint = tint, Motion = Body }, bones);

        (SkinnedMeshHandle handle, int boneCount, Matrix4x4 world, Color drawnTint) = Assert.Single(legacy.Skinned);
        Assert.Equal(2, handle.Index);
        Assert.Equal(bones.Length, boneCount);
        Assert.Equal(World, world);
        Assert.Equal(tint, drawnTint);
    }

    [Fact]
    public void The_defaults_route_a_dissolving_descriptor_through_the_dissolved_draws()
    {
        var dissolving = new DissolvingTileWorldScene();
        ITileWorldScene scene = dissolving;
        var tube = new SkinnedMeshHandle(2, 1);
        Matrix4x4[] bones = { Matrix4x4.Identity };

        scene.DrawMesh(new RigidInstanceDraw(Handle, World) { Dissolve = 0.4f, Motion = Body });
        scene.DrawMesh(new RigidInstanceDraw(Handle, World) { Motion = Body });
        scene.DrawSkinned(new SkinnedInstanceDraw(tube, World) { Dissolve = 0.3f, Motion = Body }, bones);
        scene.DrawSkinned(new SkinnedInstanceDraw(tube, World) { Motion = Body }, bones);

        Assert.Equal(new[] { 0.4f }, dissolving.MeshDissolves);
        Assert.Equal(new[] { 0.3f }, dissolving.SkinnedDissolves);
        Assert.Equal((1, 1), (dissolving.PlainMeshes, dissolving.PlainSkinned));
    }

    // Overrides both dissolved members, so each descriptor default's dissolve branch is observed rather than hidden
    // behind the solid fallback the legacy scene inherits.
    sealed class DissolvingTileWorldScene : ITileWorldScene
    {
        public List<float> MeshDissolves { get; } = new();
        public List<float> SkinnedDissolves { get; } = new();
        public int PlainMeshes { get; private set; }
        public int PlainSkinned { get; private set; }

        public MeshHandle LoadMesh(GltfMesh mesh) => default;
        public void UnloadMesh(MeshHandle handle) { }
        public void DrawMesh(MeshHandle handle, Matrix4x4 world) => PlainMeshes++;
        public void DrawMeshDissolved(MeshHandle handle, Matrix4x4 world, float dissolve, float edgeWidth, Color edgeColor) =>
            MeshDissolves.Add(dissolve);
        public void DrawSkinned(SkinnedMeshHandle handle, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 world,
            Color tint) => PlainSkinned++;
        public void DrawSkinnedDissolved(SkinnedMeshHandle handle, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 world,
            Color tint, float dissolve, float edgeWidth, Color edgeColor) => SkinnedDissolves.Add(dissolve);
        public IReadOnlyList<MeshHandle> LoadPropMeshes(IReadOnlyList<GltfMeshPart> parts) =>
            Array.Empty<MeshHandle>();
        public void UnloadPropMeshes(IReadOnlyList<MeshHandle> handles) { }
        public int DrawProps(IReadOnlyList<PropPlacement> placements,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts, Vector3 focus, float drawRadius) => 0;
    }

    sealed class LegacyTileWorldScene : ITileWorldScene
    {
        public List<(MeshHandle Handle, Matrix4x4 World)> Drawn { get; } = new();
        public List<(SkinnedMeshHandle Handle, int Bones, Matrix4x4 World, Color Tint)> Skinned { get; } = new();

        public MeshHandle LoadMesh(GltfMesh mesh) => default;
        public void UnloadMesh(MeshHandle handle) { }
        public void DrawMesh(MeshHandle handle, Matrix4x4 world) => Drawn.Add((handle, world));
        public void DrawSkinned(SkinnedMeshHandle handle, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 world,
            Color tint) => Skinned.Add((handle, boneMatrices.Length, world, tint));
        public IReadOnlyList<MeshHandle> LoadPropMeshes(IReadOnlyList<GltfMeshPart> parts) =>
            Array.Empty<MeshHandle>();
        public void UnloadPropMeshes(IReadOnlyList<MeshHandle> handles) { }
        public int DrawProps(IReadOnlyList<PropPlacement> placements,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts, Vector3 focus, float drawRadius) => 0;
    }
}
