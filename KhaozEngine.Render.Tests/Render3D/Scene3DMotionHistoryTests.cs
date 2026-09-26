using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// How <see cref="Scene3D"/> drives its motion history (TEMPORAL-FOUNDATIONS-DESIGN section 3). It swaps at
/// <see cref="Scene3D.Begin"/> and records keyed submissions only while temporal rendering is active. With temporal
/// off it creates nothing and records nothing, and a steady frame allocates nothing either way.
/// </summary>
[Collection("AllocSensitive")]   // two cases are zero-allocation readings (#264)
public sealed class Scene3DMotionHistoryTests
{
    static readonly MeshHandle Box = new(1, 1);
    static readonly MotionKey Sword = MotionKey.Combine(MotionKey.From(501), 1);

    [Fact]
    public void With_temporal_inactive_keyed_draws_create_and_record_nothing()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        for (int frame = 0; frame < 3; frame++)
        {
            scene.Begin();
            scene.Draw(new RigidInstanceDraw(Box, Matrix4x4.CreateTranslation(frame, 0f, 0f)) { Motion = Sword });
            scene.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.Identity) { Motion = MotionKey.From(7) },
                mesh.RestPose);
        }

        Assert.Null(scene.MotionHistoryForTests);
        Assert.Null(scene.ActiveMotionHistory);
        Assert.Equal((0, 0, 0), scene.MotionKeyCounts);
    }

    [Fact]
    public void With_temporal_active_last_frames_keyed_transform_is_the_previous_state()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        Matrix4x4 first = Matrix4x4.CreateTranslation(1000f, 0f, -2000f);
        Matrix4x4 second = Matrix4x4.CreateTranslation(1001f, 0f, -2000f);

        scene.Begin();
        scene.Draw(new RigidInstanceDraw(Box, first) { Motion = Sword });
        Assert.False(scene.ActiveMotionHistory!.TryGetPreviousRigid(Sword, out _));   // first sighting

        scene.Begin();
        scene.Draw(new RigidInstanceDraw(Box, second) { Motion = Sword });
        Assert.True(scene.ActiveMotionHistory!.TryGetPreviousRigid(Sword, out Matrix4x4 previous));
        Assert.Equal(first, previous);   // absolute, exactly as submitted: the render origin plays no part
        Assert.Equal((1, 0, 0), scene.MotionKeyCounts);
    }

    [Fact]
    public void Unkeyed_draws_old_overloads_and_shadow_only_draws_record_nothing()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);

        scene.Begin();
        scene.Draw(Box, Matrix4x4.Identity);
        scene.Draw(new RigidInstanceDraw(Box, Matrix4x4.Identity));
        scene.Draw(new RigidInstanceDraw(Box, Matrix4x4.Identity) { ShadowOnly = true, Motion = Sword });
        scene.DrawSkinned(tube, mesh.RestPose, Matrix4x4.Identity, KhaozEngine.Primitives.Color.White);
        Assert.Equal((0, 0, 0), scene.MotionKeyCounts);

        scene.Begin();
        Assert.False(scene.ActiveMotionHistory!.TryGetPreviousRigid(Sword, out _));
    }

    [Fact]
    public void A_keyed_skinned_draw_records_its_model_and_composed_palette_from_its_own_slot()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        Matrix4x4[] bones = MotionTestScene.Bent(mesh, 0.5f);
        Matrix4x4 model = Matrix4x4.CreateTranslation(-500f, 3f, 250f);
        MotionKey body = MotionKey.From(12);

        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.Identity), mesh.RestPose);   // unkeyed, slot 0
        scene.DrawSkinned(new SkinnedInstanceDraw(tube, model) { Motion = body }, bones);      // keyed, slot 1
        Assert.Equal((0, 1, 0), scene.MotionKeyCounts);

        scene.Begin();
        Assert.True(scene.ActiveMotionHistory!.TryGetPreviousSkinned(body, out Matrix4x4 previousModel,
            out ReadOnlySpan<Matrix4x4> palette));
        Assert.Equal(model, previousModel);
        Assert.Equal(bones.Length, palette.Length);
        for (int b = 0; b < bones.Length; b++)
            Assert.Equal(SkinningMath.Compose(bones[b], mesh.InverseBind[b]), palette[b]);
    }

    [Fact]
    public void A_stale_skinned_handle_records_nothing()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        scene.UnloadSkinnedMesh(tube);

        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.Identity) { Motion = MotionKey.From(5) }, mesh.RestPose);
        Assert.Equal((0, 0, 0), scene.MotionKeyCounts);
    }

    [Fact]
    public void Turning_temporal_off_forgets_the_history_so_the_next_active_frame_starts_fresh()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        scene.Begin();
        scene.Draw(new RigidInstanceDraw(Box, Matrix4x4.Identity) { Motion = Sword });

        scene.ForceTemporalForTests = false;
        scene.Begin();
        Assert.Null(scene.ActiveMotionHistory);

        scene.ForceTemporalForTests = true;
        scene.Begin();
        Assert.False(scene.ActiveMotionHistory!.TryGetPreviousRigid(Sword, out _));
    }

    [Fact]
    public void A_requester_set_after_Begin_records_that_frames_keyed_draws()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        Matrix4x4 first = Matrix4x4.CreateTranslation(3f, 0f, 4f);

        scene.Begin();                        // temporal off at Begin
        scene.ForceTemporalForTests = true;   // before the frame's first render, so this frame is temporal
        scene.Draw(new RigidInstanceDraw(Box, first) { Motion = Sword });
        Assert.Equal((1, 0, 0), scene.MotionKeyCounts);

        scene.Begin();
        Assert.True(scene.ActiveMotionHistory!.TryGetPreviousRigid(Sword, out Matrix4x4 previous));
        Assert.Equal(first, previous);
    }

    [Fact]
    public void A_requester_cleared_after_the_frames_first_render_still_records_that_frame()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        Matrix4x4 first = Matrix4x4.CreateTranslation(-6f, 0f, 2f);

        scene.Begin();
        harness.Render();                      // the frame's first render fixes it as temporal
        scene.ForceTemporalForTests = false;   // takes effect on the next frame
        scene.Draw(new RigidInstanceDraw(Box, first) { Motion = Sword });
        Assert.Equal((1, 0, 0), scene.MotionKeyCounts);

        scene.ForceTemporalForTests = true;
        scene.Begin();
        Assert.True(scene.ActiveMotionHistory!.TryGetPreviousRigid(Sword, out Matrix4x4 previous));
        Assert.Equal(first, previous);
    }

    [Fact]
    public void A_steady_temporal_frame_of_keyed_submissions_allocates_nothing()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        Matrix4x4[] bones = MotionTestScene.Bent(mesh, 0.25f);
        MotionKey crowd = MotionKey.From(900);

        void Frame()
        {
            scene.Begin();
            for (uint i = 0; i < 500; i++)
                scene.Draw(new RigidInstanceDraw(Box, Matrix4x4.CreateTranslation(i, 0f, 0f))
                    { Motion = MotionKey.Combine(crowd, i) });
            for (uint i = 0; i < 16; i++)
                scene.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.CreateTranslation(0f, 0f, i))
                    { Motion = MotionKey.Combine(crowd, 1_000 + i) }, bones);
        }

        for (int i = 0; i < 4; i++) Frame();
        Assert.Equal((500, 16, 0), scene.MotionKeyCounts);   // not vacuous: every draw is keyed and recorded

        // Retries once before failing (see AllocAssert.NoPerCallAllocation), per issue #284.
        AllocAssert.NoPerCallAllocation("20 steady temporal frames of 500 keyed rigid and 16 keyed skinned draws", () =>
        {
            for (int i = 0; i < 20; i++) Frame();
        });
    }

    [Fact]
    public void A_steady_frame_with_temporal_inactive_allocates_nothing_and_creates_no_history()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        MotionKey crowd = MotionKey.From(901);

        void Frame()
        {
            scene.Begin();
            for (uint i = 0; i < 500; i++)
                scene.Draw(new RigidInstanceDraw(Box, Matrix4x4.CreateTranslation(i, 0f, 0f))
                    { Motion = MotionKey.Combine(crowd, i) });
            for (uint i = 0; i < 16; i++)
                scene.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.Identity)
                    { Motion = MotionKey.Combine(crowd, 1_000 + i) }, mesh.RestPose);
        }

        for (int i = 0; i < 4; i++) Frame();
        AllocAssert.NoPerCallAllocation("20 steady frames of keyed draws with temporal off", () =>
        {
            for (int i = 0; i < 20; i++) Frame();
        });
        Assert.Null(scene.MotionHistoryForTests);
    }
}
