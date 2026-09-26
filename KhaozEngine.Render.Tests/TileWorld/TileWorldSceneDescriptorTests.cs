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
/// had, carrying every field those draws take, and draws nothing or refuses exactly where the scene's queue
/// does.</summary>
public sealed class TileWorldSceneDescriptorTests
{
    static readonly MeshHandle Handle = new(7, 3);
    static readonly SkinnedMeshHandle Tube = new(2, 1);
    static readonly Matrix4x4 World = Matrix4x4.CreateTranslation(4f, 5f, 6f);
    static readonly Color Tint = new(0.2f, 0.3f, 0.4f, 1f);
    static readonly Color Edge = new(0.9f, 0.3f, 0.1f, 1f);
    static readonly Matrix4x4[] Bones = { Matrix4x4.CreateTranslation(1f, 2f, 3f), Matrix4x4.CreateRotationZ(0.5f) };
    static readonly MotionKey Body = MotionKey.Combine(MotionKey.From(4242), 3);

    // Every field a fallback carries is distinct from its default, so a route that forwards the wrong one shows.
    static RigidInstanceDraw Rigid(float dissolve) => new(Handle, World)
    {
        Tint = Tint, Dissolve = dissolve, DissolveEdgeWidth = 0.14f, DissolveEdgeColor = Edge, Motion = Body,
    };

    static SkinnedInstanceDraw Skinned(float dissolve) => new(Tube, World)
    {
        Tint = Tint, Dissolve = dissolve, DissolveEdgeWidth = 0.12f, DissolveEdgeColor = Edge, Motion = Body,
    };

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
    public void The_adapter_records_both_keyed_descriptors_for_temporal_rendering()
    {
        using var harness = new MotionTestScene();
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        var adapter = new Scene3DTileWorldScene(harness.Scene);
        harness.Scene.ForceTemporalForTests = true;

        harness.Scene.Begin();
        adapter.DrawMesh(new RigidInstanceDraw(Handle, World) { Motion = Body });
        adapter.DrawSkinned(new SkinnedInstanceDraw(tube, World) { Motion = Body }, mesh.RestPose);

        Assert.Equal((1, 1, 0), harness.Scene.MotionKeyCounts);
    }

    [Fact]
    public void A_legacy_scene_falls_back_to_the_mesh_draws_and_skips_shadow_only()
    {
        var legacy = new LegacyTileWorldScene();
        ITileWorldScene scene = legacy;

        scene.DrawMesh(Rigid(0f));
        scene.DrawMesh(Rigid(0.4f));   // DrawMeshDissolved's default is solid
        scene.DrawMesh(new RigidInstanceDraw(Handle, World) { ShadowOnly = true });   // never seen, so nothing

        Assert.Equal(new[] { (Handle, World), (Handle, World) }, legacy.Drawn);
    }

    [Fact]
    public void A_shadow_only_descriptor_that_casts_nothing_throws_the_queue_refusal()
    {
        var nowhere = new RigidInstanceDraw(Handle, World) { ShadowOnly = true, CastsShadows = false };
        using var harness = new MotionTestScene();
        harness.Scene.Begin();
        ArgumentException queue = Assert.Throws<ArgumentException>(() => harness.Scene.Draw(nowhere));
        var legacy = new LegacyTileWorldScene();
        ITileWorldScene scene = legacy;

        ArgumentException fallback = Assert.Throws<ArgumentException>(() => scene.DrawMesh(nowhere));

        Assert.Equal("shadowOnly", fallback.ParamName);
        Assert.Equal(queue.ParamName, fallback.ParamName);
        Assert.Equal(queue.Message, fallback.Message);
        Assert.Empty(legacy.Drawn);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.25f)]
    public void A_complement_phase_with_no_dissolve_draws_nothing(float dissolve)
    {
        var legacy = new LegacyTileWorldScene();
        var dissolving = new DissolvingTileWorldScene();
        RigidInstanceDraw emptyKeepSet = Rigid(dissolve) with { DissolveComplement = 1f };

        ((ITileWorldScene)legacy).DrawMesh(emptyKeepSet);
        ((ITileWorldScene)dissolving).DrawMesh(emptyKeepSet);

        Assert.Empty(legacy.Drawn);
        Assert.Empty(dissolving.Meshes);
        Assert.Empty(dissolving.MeshDissolves);
    }

    // The shaders take the complement only above one half, so a lower phase with no dissolve is an ordinary solid draw.
    [Theory]
    [InlineData(0.3f)]
    [InlineData(0.5f)]
    public void A_complement_phase_at_or_below_one_half_with_no_dissolve_draws_solid(float complement)
    {
        var legacy = new LegacyTileWorldScene();
        var dissolving = new DissolvingTileWorldScene();
        RigidInstanceDraw ordinary = Rigid(0f) with { DissolveComplement = complement };

        ((ITileWorldScene)legacy).DrawMesh(ordinary);
        ((ITileWorldScene)dissolving).DrawMesh(ordinary);

        Assert.Equal(new[] { (Handle, World) }, legacy.Drawn);
        Assert.Equal(new[] { (Handle, World) }, dissolving.Meshes);
        Assert.Empty(dissolving.MeshDissolves);
    }

    [Fact]
    public void A_legacy_skinned_scene_falls_back_to_the_plain_skinned_draw()
    {
        var legacy = new LegacyTileWorldScene();
        ITileWorldScene scene = legacy;

        scene.DrawSkinned(Skinned(0f), Bones);

        (SkinnedMeshHandle handle, Matrix4x4[] bones, Matrix4x4 world, Color tint) = Assert.Single(legacy.Skinned);
        Assert.Equal(Tube, handle);
        Assert.Equal(Bones, bones);
        Assert.Equal(World, world);
        Assert.Equal(Tint, tint);
    }

    [Fact]
    public void A_scene_with_only_the_plain_skinned_draw_refuses_a_dissolving_descriptor()
    {
        var legacy = new LegacyTileWorldScene();
        ITileWorldScene scene = legacy;

        Assert.Throws<NotSupportedException>(() => scene.DrawSkinned(Skinned(0.3f), Bones));
        Assert.Empty(legacy.Skinned);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.3f)]
    public void A_scene_without_skinning_refuses_the_skinned_descriptor(float dissolve)
    {
        ITileWorldScene scene = new RigidOnlyTileWorldScene();

        Assert.Throws<NotSupportedException>(() => scene.DrawSkinned(Skinned(dissolve), Bones));
    }

    [Fact]
    public void The_defaults_route_a_dissolving_descriptor_through_the_dissolved_draws()
    {
        var dissolving = new DissolvingTileWorldScene();
        ITileWorldScene scene = dissolving;

        scene.DrawMesh(Rigid(0.4f) with { DissolveComplement = 1f, InvertShadowDissolve = true });   // both dropped
        scene.DrawMesh(Rigid(0f));
        scene.DrawSkinned(Skinned(0.3f), Bones);
        scene.DrawSkinned(Skinned(0f), Bones);

        Assert.Equal(new[] { (Handle, World, 0.4f, 0.14f, Edge) }, dissolving.MeshDissolves);
        Assert.Equal(new[] { (Handle, World) }, dissolving.Meshes);

        var dissolved = Assert.Single(dissolving.SkinnedDissolves);
        Assert.Equal(Tube, dissolved.Handle);
        Assert.Equal(Bones, dissolved.Bones);
        Assert.Equal((World, Tint, 0.3f, 0.12f, Edge),
            (dissolved.World, dissolved.Tint, dissolved.Dissolve, dissolved.EdgeWidth, dissolved.EdgeColor));

        (SkinnedMeshHandle handle, Matrix4x4[] bones, Matrix4x4 world, Color tint) = Assert.Single(dissolving.Skinned);
        Assert.Equal(Tube, handle);
        Assert.Equal(Bones, bones);
        Assert.Equal((World, Tint), (world, tint));
    }

    // Overrides both dissolved members, so each descriptor default's dissolve branch is observed rather than hidden
    // behind the solid fallback the legacy scene inherits. Records every argument of every draw.
    sealed class DissolvingTileWorldScene : ITileWorldScene
    {
        public List<(MeshHandle Handle, Matrix4x4 World)> Meshes { get; } = new();
        public List<(MeshHandle Handle, Matrix4x4 World, float Dissolve, float EdgeWidth, Color EdgeColor)>
            MeshDissolves { get; } = new();
        public List<(SkinnedMeshHandle Handle, Matrix4x4[] Bones, Matrix4x4 World, Color Tint)> Skinned { get; } = new();
        public List<(SkinnedMeshHandle Handle, Matrix4x4[] Bones, Matrix4x4 World, Color Tint, float Dissolve,
            float EdgeWidth, Color EdgeColor)> SkinnedDissolves { get; } = new();

        public MeshHandle LoadMesh(GltfMesh mesh) => default;
        public void UnloadMesh(MeshHandle handle) { }
        public void DrawMesh(MeshHandle handle, Matrix4x4 world) => Meshes.Add((handle, world));
        public void DrawMeshDissolved(MeshHandle handle, Matrix4x4 world, float dissolve, float edgeWidth,
            Color edgeColor) => MeshDissolves.Add((handle, world, dissolve, edgeWidth, edgeColor));
        public void DrawSkinned(SkinnedMeshHandle handle, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 world,
            Color tint) => Skinned.Add((handle, boneMatrices.ToArray(), world, tint));
        public void DrawSkinnedDissolved(SkinnedMeshHandle handle, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 world,
            Color tint, float dissolve, float edgeWidth, Color edgeColor) =>
            SkinnedDissolves.Add((handle, boneMatrices.ToArray(), world, tint, dissolve, edgeWidth, edgeColor));
        public IReadOnlyList<MeshHandle> LoadPropMeshes(IReadOnlyList<GltfMeshPart> parts) =>
            Array.Empty<MeshHandle>();
        public void UnloadPropMeshes(IReadOnlyList<MeshHandle> handles) { }
        public int DrawProps(IReadOnlyList<PropPlacement> placements,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts, Vector3 focus, float drawRadius) => 0;
    }

    // Implements the plain mesh and skinned draws alone, the shape of a scene written before either dissolve existed.
    sealed class LegacyTileWorldScene : ITileWorldScene
    {
        public List<(MeshHandle Handle, Matrix4x4 World)> Drawn { get; } = new();
        public List<(SkinnedMeshHandle Handle, Matrix4x4[] Bones, Matrix4x4 World, Color Tint)> Skinned { get; } = new();

        public MeshHandle LoadMesh(GltfMesh mesh) => default;
        public void UnloadMesh(MeshHandle handle) { }
        public void DrawMesh(MeshHandle handle, Matrix4x4 world) => Drawn.Add((handle, world));
        public void DrawSkinned(SkinnedMeshHandle handle, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 world,
            Color tint) => Skinned.Add((handle, boneMatrices.ToArray(), world, tint));
        public IReadOnlyList<MeshHandle> LoadPropMeshes(IReadOnlyList<GltfMeshPart> parts) =>
            Array.Empty<MeshHandle>();
        public void UnloadPropMeshes(IReadOnlyList<MeshHandle> handles) { }
        public int DrawProps(IReadOnlyList<PropPlacement> placements,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts, Vector3 focus, float drawRadius) => 0;
    }

    // Implements only the members the seam requires, so every skinned draw reaches a refusing default.
    sealed class RigidOnlyTileWorldScene : ITileWorldScene
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
