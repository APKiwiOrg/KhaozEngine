using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class DqMotionHistoryTests
{
    static readonly MotionKey A = MotionKey.From(41), B = MotionKey.From(42);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_bone_count_replacement_seeds_only_the_affected_actor(bool gpu)
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.UseGpuSkinning = gpu;
        scene.FrustumCulling = false;
        scene.RenderOrigin = Vector3.Zero;
        scene.ForceTemporalForTests = true;
        SkinnedGltfMesh mesh = Mesh();
        SkinnedMeshHandle first = scene.LoadSkinnedMesh(mesh), unchanged = scene.LoadSkinnedMesh(mesh);

        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(first, Matrix4x4.Identity) { Motion = A },
            [Matrix4x4.CreateTranslation(3, 0, 0)]);
        scene.DrawSkinned(new SkinnedInstanceDraw(unchanged, Matrix4x4.CreateTranslation(10, 0, 0)) { Motion = B },
            [Matrix4x4.CreateTranslation(0, 4, 0)]);
        harness.Render();
        if (!gpu) Assert.Equal(new Vector3(4, 0, 0), PreviousPositions(scene)[0]);

        scene.UnloadSkinnedMesh(first);
        SkinnedMeshHandle replacement = scene.LoadSkinnedMesh(mesh);
        Assert.Equal(first.Index, replacement.Index);
        Assert.NotEqual(first.Generation, replacement.Generation);
        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(replacement, Matrix4x4.CreateTranslation(20, 0, 0)) { Motion = A }, mesh.RestPose);
        scene.DrawSkinned(new SkinnedInstanceDraw(unchanged, Matrix4x4.CreateTranslation(30, 0, 0)) { Motion = B }, mesh.RestPose);
        harness.Render();

        if (gpu)
        {
            var image = (byte[])DqGpuReadback.Field(scene.MotionResourcesForTests!.SkinnedPalette, "_image");
            Assert.Equal(Matrix4x4.CreateTranslation(20, 0, 0), MemoryMarshal.Read<Matrix4x4>(image));
            Assert.Equal(Matrix4x4.Identity, MemoryMarshal.Read<Matrix4x4>(image.AsSpan(64)));
            Assert.Equal(Matrix4x4.CreateTranslation(10, 0, 0),
                MemoryMarshal.Read<Matrix4x4>(image.AsSpan((int)SkinnedMotionPalette.SlotBytes)));
            Assert.Equal(Matrix4x4.CreateTranslation(0, 4, 0),
                MemoryMarshal.Read<Matrix4x4>(image.AsSpan((int)SkinnedMotionPalette.SlotBytes + 64)));
        }
        else
        {
            Vector3[] previous = PreviousPositions(scene);
            Assert.Equal(new Vector3(21, 0, 0), previous[0]);
            Assert.Equal(new Vector3(11, 4, 0), previous[3]);
        }
        MotionHistory history = scene.ActiveMotionHistory!;
        Assert.True(history.TryGetPreviousBounds(A, out MeshBounds oldBounds));
        Assert.True(history.TryGetCurrentBounds(A, out MeshBounds currentBounds));
        AssertContains(oldBounds, [new(4, 0, 0), new(3, 1, 0), new(3, 0, 0)]);
        AssertContains(currentBounds, [new(21, 0, 0), new(20, 1, 0), new(20, 0, 0)]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LBS_history_contains_previous_and_current_large_joint_motion(bool gpu, bool scale)
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.UseGpuSkinning = gpu;
        scene.ForceTemporalForTests = true;
        SkinnedGltfMesh mesh = Mesh();
        // The bound must use prepared inverseBind * jointWorld, not the supplied joint alone.
        mesh.InverseBind[0] = Matrix4x4.CreateTranslation(5, -7, 3);
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(mesh);
        Matrix4x4 oldJoint = (scale ? Matrix4x4.CreateScale(32, 17, 9) : Matrix4x4.Identity)
            * Matrix4x4.CreateTranslation(500, -400, 250);
        Matrix4x4 currentJoint = (scale ? Matrix4x4.CreateScale(72, 4, 18) : Matrix4x4.Identity)
            * Matrix4x4.CreateTranslation(-600, 800, -300);
        Matrix4x4 oldWorld = Matrix4x4.CreateScale(2, -3, 4) * Matrix4x4.CreateTranslation(20, 30, -40);
        oldWorld.M21 = .5f;
        Matrix4x4 currentWorld = Matrix4x4.CreateScale(-4, 2, 3) * Matrix4x4.CreateTranslation(-50, 60, 70);
        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(handle, oldWorld) { Motion = A }, [oldJoint]);
        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(handle, currentWorld) { Motion = A }, [currentJoint]);

        MotionHistory history = scene.ActiveMotionHistory!;
        Assert.True(history.TryGetPreviousBounds(A, out MeshBounds previous));
        Assert.True(history.TryGetCurrentBounds(A, out MeshBounds current));
        AssertContainsLbs(previous, mesh, [mesh.InverseBind[0] * oldJoint], oldWorld);
        AssertContainsLbs(current, mesh, [mesh.InverseBind[0] * currentJoint], currentWorld);
    }

    [Theory]
    [InlineData(2f, -1f, 301f)]
    [InlineData(3f, 0f, 303f)]
    [InlineData(.25f, .75f, -49f)]
    [InlineData(2f, -1.5f, 350.5f)]
    [InlineData(0f, 0f, 1f)]
    [InlineData(1e-9f, 0f, 1f)]
    public void LBS_history_contains_raw_weight_results_without_changing_admission(float x, float y, float expectedX)
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        SkinnedVertex[] vertices = [Vertex(Vector3.UnitX), Vertex(-Vector3.UnitY), Vertex(Vector3.Zero)];
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].BoneIndices = new Vector4(0, 1, 0, 1);
            vertices[i].BoneWeights = new Vector4(x, y, 0, 0);
        }
        var mesh = new SkinnedGltfMesh(vertices, new ushort[] { 0, 1, 2 },
            [Matrix4x4.Identity, Matrix4x4.Identity], [Matrix4x4.Identity, Matrix4x4.Identity]);
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(mesh);
        Matrix4x4[] palette = [Matrix4x4.CreateTranslation(100, 0, 0), Matrix4x4.CreateTranslation(-100, 0, 0)];
        Assert.Equal(expectedX, SkinningMath.SkinVertex(vertices[0], palette).Position.X);
        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(handle, Matrix4x4.Identity) { Motion = A }, palette);
        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(handle, Matrix4x4.CreateTranslation(20, 0, 0)) { Motion = A }, palette);
        Assert.True(scene.ActiveMotionHistory!.TryGetPreviousBounds(A, out MeshBounds previous));
        Assert.True(scene.ActiveMotionHistory.TryGetCurrentBounds(A, out MeshBounds current));
        AssertContainsLbs(previous, mesh, palette, Matrix4x4.Identity);
        AssertContainsLbs(current, mesh, palette, Matrix4x4.CreateTranslation(20, 0, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Method_replacement_retains_boxes_containing_both_posed_surfaces(bool previousDq)
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        SkinnedGltfMesh oldMesh = Mesh(previousDq ? SkinningProfile.DualQuaternionRigid : SkinningProfile.LinearBlend);
        SkinnedGltfMesh currentMesh = Mesh(previousDq ? SkinningProfile.LinearBlend : SkinningProfile.DualQuaternionRigid);
        SkinnedMeshHandle oldHandle = scene.LoadSkinnedMesh(oldMesh), currentHandle = scene.LoadSkinnedMesh(currentMesh);
        scene.Begin();
        RecordAtSeam(scene, oldMesh, oldHandle, Matrix4x4.CreateTranslation(10, 0, 0),
            Matrix4x4.CreateTranslation(300, -400, 500));
        scene.Begin();
        RecordAtSeam(scene, currentMesh, currentHandle, Matrix4x4.CreateTranslation(-20, 0, 0),
            Matrix4x4.CreateTranslation(-600, 700, -800));
        ulong source = ((ulong)(uint)currentHandle.Generation << 32) | (uint)currentHandle.Index;
        var identity = new SkinnedMotionIdentity(currentHandle.Index, currentHandle.Generation, source, currentMesh.Profile);
        MotionHistory history = scene.ActiveMotionHistory!;
        Assert.False(history.TryGetPreviousSkinned(A, identity, out _, out _, out _));
        Assert.True(history.TryGetPreviousBounds(A, out MeshBounds previous));
        Assert.True(history.TryGetCurrentBounds(A, out MeshBounds current));
        AssertContains(previous, [new(311, -400, 500), new(310, -399, 500), new(310, -400, 500)]);
        AssertContains(current, [new(-619, 700, -800), new(-620, 701, -800), new(-620, 700, -800)]);
    }

    [Fact]
    public void Scene_CPU_history_reduces_previous_world_against_the_current_origin_on_each_render()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.UseGpuSkinning = false;
        scene.FrustumCulling = false;
        scene.ForceTemporalForTests = true;
        scene.RenderOrigin = new Vector3(896, 0, -1920);
        SkinnedGltfMesh mesh = Mesh();
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(mesh);
        Matrix4x4 oldWorld = Matrix4x4.CreateTranslation(1000, 0, -2000);
        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(handle, oldWorld) { Motion = A },
            [Matrix4x4.CreateTranslation(2, 4, 6)]);
        harness.Render();

        scene.RenderOrigin = new Vector3(1024, 0, -2048);
        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(handle, Matrix4x4.Identity) { Motion = A }, mesh.RestPose);
        harness.Render();
        Assert.NotNull(scene.PreviousFrameView);
        Assert.Equal(new Vector3(-21, 4, 54), PreviousPositions(scene)[0]);
        harness.Render();
        Assert.Equal(new Vector3(-21, 4, 54), PreviousPositions(scene)[0]);
        Assert.True(scene.ActiveMotionHistory!.TryGetPreviousSkinned(A, out Matrix4x4 stored, out _));
        Assert.Equal(oldWorld, stored);
    }

    [Fact]
    public void Scene_DQ_recording_retains_the_uploaded_identity_prepared_words_and_absolute_posed_bounds()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        scene.RenderOrigin = new Vector3(1024, 0, -2048);
        SkinnedGltfMesh mesh = Mesh(SkinningProfile.DualQuaternionRigid);
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(mesh);
        var draw = new SkinnedInstanceDraw(handle, Matrix4x4.CreateTranslation(1000, 0, -2000)) { Motion = A };
        scene.Begin();
        Matrix4x4 joint = new(-1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1, 0, 2, 4, 6, 1);
        // Stage 3 keeps public DQ submission gated until all consumers exist. Exercise the recording seam directly.
        PreparedSkinningPose pose = DqGpuReadback.Queue(scene, mesh, draw, [joint]);
        var entries = (System.Collections.IList)DqGpuReadback.Field(scene, "_skinnedMeshes");
        object entry = entries[handle.Index]!;
        MethodInfo boundsMethod = typeof(Scene3D).GetMethod("SkinnedMotionBounds", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var bounds = (MeshBounds)boundsMethod.Invoke(scene, [entry, pose, draw])!;
        Assert.InRange(1001f, bounds.Min.X, bounds.Max.X);
        Assert.InRange(4f, bounds.Min.Y, bounds.Max.Y);
        Assert.InRange(-1994f, bounds.Min.Z, bounds.Max.Z);
        Assert.Equal(new Vector3(1000, 0, -2000), bounds.Center);
        ((List<Matrix4x4>)DqGpuReadback.Field(scene, "_boneMatrices"))[0] = Matrix4x4.CreateScale(99);
        typeof(Scene3D).GetMethod("RecordSkinnedMotion", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scene, [draw, pose, bounds]);
        uint[] expected = MemoryMarshal.Cast<RigidDualQuaternion, uint>(pose.Owner.Dq(pose)).ToArray();
        scene.Begin();

        ulong sourceId = ((ulong)(uint)handle.Generation << 32) | (uint)handle.Index;
        var identity = new SkinnedMotionIdentity(handle.Index, handle.Generation, sourceId, mesh.Profile);
        Assert.True(scene.ActiveMotionHistory!.TryGetPreviousSkinned(A, identity, out Matrix4x4 world,
            out ReadOnlySpan<Matrix4x4> lbs, out ReadOnlySpan<RigidDualQuaternion> dq));
        Assert.Equal(draw.Model, world);
        Assert.True(lbs.IsEmpty);
        Assert.Equal(expected, MemoryMarshal.Cast<RigidDualQuaternion, uint>(dq).ToArray());
        Assert.True(scene.ActiveMotionHistory.TryGetPreviousBounds(A, out MeshBounds previousBounds));
        Assert.Equal(bounds, previousBounds);
    }

    [Fact]
    public void Previous_DQ_words_are_copied_not_reextracted()
    {
        var history = new MotionHistory();
        var identity = new SkinnedMotionIdentity(2, 3, 19, SkinningProfile.DualQuaternionRigid);
        RigidDualQuaternion[] dq = [RotatedAndTranslated()];
        var bounds = new MeshBounds(new Vector3(-2), new Vector3(8));
        Matrix4x4 world = Matrix4x4.CreateTranslation(1000, 0, -2000);
        history.BeginFrame();
        history.RecordSkinned(A, world, identity, default, dq, bounds);
        dq[0] = IdentityDq();
        history.BeginFrame();
        history.RecordSkinned(A, Matrix4x4.Identity, identity, default, dq, MeshBounds.Empty);

        Assert.True(history.TryGetPreviousSkinned(A, identity, out Matrix4x4 previousWorld,
            out ReadOnlySpan<Matrix4x4> lbs, out ReadOnlySpan<RigidDualQuaternion> previousDq));
        Assert.Equal(world, previousWorld);
        Assert.True(lbs.IsEmpty);
        Assert.Equal(new uint[] { 0x80000000, 0, 0x3f800000, 0, 0x40000000, 0xbf800000, 0, 0xc0400000 },
            MemoryMarshal.Cast<RigidDualQuaternion, uint>(previousDq).ToArray());
        Assert.True(history.TryGetPreviousBounds(A, out MeshBounds old));
        Assert.Equal(bounds, old);
        Assert.False(history.TryGetPreviousSkinned(A, out _, out _));
    }

    [Theory]
    [InlineData("mesh")]
    [InlineData("generation")]
    [InlineData("source")]
    [InlineData("method")]
    public void Identity_replacement_rejects_only_its_actor_and_keeps_both_bounds(string change)
    {
        var history = new MotionHistory();
        var original = new SkinnedMotionIdentity(2, 3, 19, SkinningProfile.DualQuaternionRigid);
        var unchanged = new SkinnedMotionIdentity(5, 2, 23, SkinningProfile.LinearBlend);
        SkinnedMotionIdentity replacement = change switch
        {
            "mesh" => original with { MeshIndex = 6 },
            "generation" => original with { Generation = 4 },
            "source" => original with { SourceIdentity = 20 },
            _ => original with { Profile = SkinningProfile.LinearBlend },
        };
        var oldBounds = new MeshBounds(new Vector3(-3), new Vector3(2));
        var currentBounds = new MeshBounds(new Vector3(8), new Vector3(10));
        history.BeginFrame();
        history.RecordSkinned(A, Matrix4x4.Identity, original, default, [IdentityDq()], oldBounds);
        history.RecordSkinned(B, Matrix4x4.CreateTranslation(9, 0, 0), unchanged,
            [Matrix4x4.CreateScale(2, 3, 4)], default, MeshBounds.Empty);
        history.BeginFrame();
        history.RecordSkinned(A, Matrix4x4.Identity, replacement,
            replacement.Profile == SkinningProfile.LinearBlend ? [Matrix4x4.Identity] : default,
            replacement.Profile == SkinningProfile.DualQuaternionRigid ? [IdentityDq()] : default, currentBounds);

        Assert.False(history.TryGetPreviousSkinned(A, in replacement, out Matrix4x4 world,
            out ReadOnlySpan<Matrix4x4> lbs, out ReadOnlySpan<RigidDualQuaternion> dq));
        Assert.Equal(default, world);
        Assert.True(lbs.IsEmpty);
        Assert.True(dq.IsEmpty);
        Assert.True(history.TryGetPreviousSkinned(B, in unchanged, out world, out lbs, out dq));
        Assert.Equal(Matrix4x4.CreateTranslation(9, 0, 0), world);
        Assert.Equal(Matrix4x4.CreateScale(2, 3, 4), Assert.Single(lbs.ToArray()));
        Assert.True(dq.IsEmpty);
        Assert.True(history.TryGetPreviousBounds(A, out MeshBounds previousBounds));
        Assert.True(history.TryGetCurrentBounds(A, out MeshBounds retainedCurrent));
        Assert.Equal(oldBounds, previousBounds);
        Assert.Equal(currentBounds, retainedCurrent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CPU_previous_positions_use_the_previous_profile(bool dq)
    {
        // Half identity, half Z180 gives Z90 under DQ and a collapsed XY basis under LBS.
        SkinnedVertex vertex = Vertex(Vector3.UnitX);
        vertex.BoneIndices = new Vector4(0, 1, 0, 0);
        vertex.BoneWeights = new Vector4(.5f, .5f, 0, 0);
        Matrix4x4 z180 = new(-1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
        var destination = new List<Vector3> { new(99) };
        Matrix4x4 previousWorld = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(10, 20, 30);

        CpuSkinnedMotion.AppendPrevious([vertex], dq ? SkinningProfile.DualQuaternionRigid : SkinningProfile.LinearBlend,
            [Matrix4x4.Identity, z180], [IdentityDq(), new RigidDualQuaternion(new Vector4(0, 0, 1, 0), Vector4.Zero)],
            previousWorld, destination);

        Assert.Equal(new[] { new Vector3(99), dq ? new Vector3(10, 23, 30) : new Vector3(10, 20, 30) }, destination);
    }

    [Theory]
    [InlineData("joint", 1, 4, 6)]
    [InlineData("placement", 11, 20, 30)]
    [InlineData("rebase", -23, 4, 54)]
    public void Previous_DQ_positions_match_independent_joint_placement_and_rebase_fixtures(
        string motion, float x, float y, float z)
    {
        var history = new MotionHistory();
        var identity = new SkinnedMotionIdentity(1, 1, 1, SkinningProfile.DualQuaternionRigid);
        Matrix4x4 previousWorld = motion switch
        {
            "placement" => Matrix4x4.CreateTranslation(10, 20, 30),
            "rebase" => Matrix4x4.CreateTranslation(1000, 0, -2000),
            _ => Matrix4x4.Identity,
        };
        history.BeginFrame();
        history.RecordSkinned(A, previousWorld, identity, default,
            [motion == "placement" ? IdentityDq() : RotatedAndTranslated()], MeshBounds.Empty);
        history.BeginFrame();
        history.RecordSkinned(A, Matrix4x4.CreateTranslation(new Vector3(100)), identity, default, [IdentityDq()], MeshBounds.Empty);
        Assert.True(history.TryGetPreviousSkinned(A, identity, out Matrix4x4 world,
            out ReadOnlySpan<Matrix4x4> lbs, out ReadOnlySpan<RigidDualQuaternion> dq));
        if (motion == "rebase")
        {
            world.M41 -= 1024;
            world.M43 -= -2048;
        }
        var positions = new List<Vector3>();
        CpuSkinnedMotion.AppendPrevious([Vertex(Vector3.UnitX)], identity.Profile, lbs, dq, world, positions);
        Assert.Equal(new Vector3(x, y, z), Assert.Single(positions));
        Assert.True(history.TryGetPreviousSkinned(A, identity, out world, out _, out _));
        Assert.Equal(previousWorld, world);
    }

    [Fact]
    public void DQ_history_keeps_collision_none_unsubmitted_and_reset_semantics()
    {
        var history = new MotionHistory();
        var first = new SkinnedMotionIdentity(1, 1, 1, SkinningProfile.DualQuaternionRigid);
        var last = first with { SourceIdentity = 2 };
        var bounds = new MeshBounds(Vector3.One, new Vector3(4));
        history.BeginFrame();
        Assert.False(history.TryGetPreviousSkinned(A, first, out _, out _, out _));
        Assert.False(history.TryGetPreviousBounds(A, out _));
        history.RecordSkinned(MotionKey.None, Matrix4x4.Identity, first, default, [IdentityDq()], bounds);
        history.RecordSkinned(A, Matrix4x4.Identity, first, default, [IdentityDq()], MeshBounds.Empty);
        history.RecordSkinned(A, Matrix4x4.CreateTranslation(2, 3, 4), last, default, [RotatedAndTranslated()], bounds);
        Assert.Equal(2, history.KeyedSkinned);
        Assert.Equal(1, history.Collisions);
        history.BeginFrame();
        Assert.False(history.TryGetPreviousSkinned(MotionKey.None, first, out _, out _, out _));
        Assert.False(history.TryGetPreviousSkinned(A, first, out _, out _, out _));
        Assert.True(history.TryGetPreviousSkinned(A, last, out Matrix4x4 world, out _, out ReadOnlySpan<RigidDualQuaternion> dq));
        Assert.Equal(Matrix4x4.CreateTranslation(2, 3, 4), world);
        Assert.Equal(RotatedAndTranslated(), Assert.Single(dq.ToArray()));
        Assert.True(history.TryGetPreviousBounds(A, out MeshBounds previous));
        Assert.Equal(bounds, previous);
        Assert.False(history.TryGetCurrentBounds(A, out _));
        history.BeginFrame();
        Assert.False(history.TryGetPreviousSkinned(A, last, out _, out _, out _));
        Assert.False(history.TryGetPreviousBounds(A, out _));
        history.RecordSkinned(A, world, last, default, [IdentityDq()], bounds);
        history.BeginFrame();
        history.RecordSkinned(A, world, last, default, [IdentityDq()], bounds);
        history.Reset();
        Assert.False(history.TryGetPreviousBounds(A, out _));
        Assert.False(history.TryGetCurrentBounds(A, out _));
        history.BeginFrame();
        Assert.False(history.TryGetPreviousSkinned(A, last, out _, out _, out _));
    }

    [Fact]
    public void Temporal_off_on_discards_skinned_identity_and_bounds()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        SkinnedGltfMesh mesh = Mesh();
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(mesh);
        scene.ForceTemporalForTests = true;
        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(handle, Matrix4x4.Identity) { Motion = A }, mesh.RestPose);
        scene.Begin();
        Assert.True(scene.ActiveMotionHistory!.TryGetPreviousBounds(A, out _));
        scene.ForceTemporalForTests = false;
        scene.Begin();
        Assert.Null(scene.ActiveMotionHistory);
        scene.ForceTemporalForTests = true;
        scene.Begin();
        Assert.False(scene.ActiveMotionHistory!.TryGetPreviousSkinned(A, out _, out _));
        Assert.False(scene.ActiveMotionHistory.TryGetPreviousBounds(A, out _));
    }

    static RigidDualQuaternion IdentityDq() => new(new Vector4(0, 0, 0, 1), Vector4.Zero);

    // Exact Z180 plus translation (2,4,6). The negative zero must survive history storage.
    static RigidDualQuaternion RotatedAndTranslated() => new(new Vector4(-0f, 0, 1, 0), new Vector4(2, -1, 0, -3));

    static SkinnedGltfMesh Mesh(SkinningProfile? profile = null) => new(
        [Vertex(Vector3.UnitX), Vertex(Vector3.UnitY), Vertex(Vector3.Zero)], new ushort[] { 0, 1, 2 },
        [Matrix4x4.Identity], [Matrix4x4.Identity], null, profile ?? SkinningProfile.LinearBlend);

    static SkinnedVertex Vertex(Vector3 position) => new()
    {
        Position = position,
        Normal = Vector3.UnitZ,
        Color = Vector4.One,
        BoneIndices = Vector4.Zero,
        BoneWeights = new Vector4(1, 0, 0, 0),
    };

    static Vector3[] PreviousPositions(Scene3D scene) => ((List<Vector3>)typeof(Scene3D)
        .GetField("_cpuSkinnedPrevious", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!).ToArray();

    static void AssertContains(MeshBounds bounds, ReadOnlySpan<Vector3> positions)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            Assert.True(float.IsFinite(bounds.Min[axis]));
            Assert.True(float.IsFinite(bounds.Max[axis]));
        }
        foreach (Vector3 position in positions)
            for (int axis = 0; axis < 3; axis++)
                Assert.InRange(position[axis], bounds.Min[axis], bounds.Max[axis]);
    }

    static void AssertContainsLbs(MeshBounds bounds, SkinnedGltfMesh mesh, Matrix4x4[] palette, Matrix4x4 world)
    {
        foreach (SkinnedVertex vertex in mesh.Vertices)
        {
            AssertContains(bounds, [Vector3.Transform(SkinningMath.SkinVertex(vertex, palette).Position, world)]);
            // The GPU retains the blended homogeneous W through model placement and projection.
            // Raw signed weights can make its Cartesian surface larger than the CPU result.
            Matrix4x4 skin = SkinningMath.BlendSkinMatrix(palette, vertex.BoneIndices, vertex.BoneWeights);
            Vector4 placed = Vector4.Transform(Vector4.Transform(new Vector4(vertex.Position, 1), skin), world);
            AssertContains(bounds, [new Vector3(placed.X, placed.Y, placed.Z) / placed.W]);
        }
    }

    static void RecordAtSeam(Scene3D scene, SkinnedGltfMesh mesh, SkinnedMeshHandle handle, Matrix4x4 world, Matrix4x4 joint)
    {
        if (mesh.Profile.Method == SkinningMethod.LinearBlend)
        {
            scene.DrawSkinned(new SkinnedInstanceDraw(handle, world) { Motion = A }, [joint]);
            return;
        }
        // The public DQ gate remains closed. Use the existing recording seam without rendering a DQ actor.
        var draw = new SkinnedInstanceDraw(handle, world) { Motion = A };
        PreparedSkinningPose pose = DqGpuReadback.Queue(scene, mesh, draw, [joint]);
        var entries = (System.Collections.IList)DqGpuReadback.Field(scene, "_skinnedMeshes");
        var bounds = (MeshBounds)typeof(Scene3D).GetMethod("SkinnedMotionBounds", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scene, [entries[handle.Index], pose, draw])!;
        typeof(Scene3D).GetMethod("RecordSkinnedMotion", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scene, [draw, pose, bounds]);
    }
}
