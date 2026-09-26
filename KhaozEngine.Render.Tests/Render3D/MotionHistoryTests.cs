using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// <see cref="MotionHistory"/> (TEMPORAL-FOUNDATIONS-DESIGN section 3): last frame's transform and palette per key, a
/// first sighting with no previous state, keys dropped when not drawn, last-wins collisions counted, and a steady
/// frame that allocates nothing.
/// </summary>
[Collection("AllocSensitive")]   // one case is a zero-allocation reading (#264)
public sealed class MotionHistoryTests
{
    static readonly MotionKey A = MotionKey.From(1), B = MotionKey.From(2);

    static Matrix4x4 At(float x) => Matrix4x4.CreateTranslation(x, 0f, 0f);

    static Matrix4x4[] Palette(int bones, float seed)
    {
        var palette = new Matrix4x4[bones];
        for (int i = 0; i < bones; i++) palette[i] = Matrix4x4.CreateRotationZ(seed + i) * At(i);
        return palette;
    }

    [Fact]
    public void Last_frames_transform_is_the_previous_state_and_a_first_sighting_has_none()
    {
        var history = new MotionHistory();
        history.BeginFrame();
        history.RecordRigid(A, At(1f));
        Assert.False(history.TryGetPreviousRigid(A, out _));

        history.BeginFrame();
        history.RecordRigid(A, At(2f));
        Assert.True(history.TryGetPreviousRigid(A, out Matrix4x4 previous));
        Assert.Equal(At(1f), previous);
    }

    [Fact]
    public void A_key_not_seen_this_frame_is_dropped_at_the_swap()
    {
        var history = new MotionHistory();
        history.BeginFrame();
        history.RecordRigid(A, At(1f));
        history.RecordRigid(B, At(5f));
        history.BeginFrame();
        history.RecordRigid(A, At(2f));   // B is not drawn this frame
        Assert.True(history.TryGetPreviousRigid(B, out _));

        history.BeginFrame();
        Assert.True(history.TryGetPreviousRigid(A, out _));
        Assert.False(history.TryGetPreviousRigid(B, out _));
    }

    [Fact]
    public void A_collision_is_counted_and_the_last_submission_wins()
    {
        var history = new MotionHistory();
        history.BeginFrame();
        history.RecordRigid(A, At(1f));
        history.RecordRigid(A, At(9f));
        history.RecordSkinned(B, At(3f), Palette(2, 0f));
        history.RecordSkinned(B, At(4f), Palette(3, 1f));
        Assert.Equal(2, history.KeyedRigid);
        Assert.Equal(2, history.KeyedSkinned);
        Assert.Equal(2, history.Collisions);

        history.BeginFrame();
        Assert.True(history.TryGetPreviousRigid(A, out Matrix4x4 rigid));
        Assert.Equal(At(9f), rigid);
        Assert.True(history.TryGetPreviousSkinned(B, out Matrix4x4 model, out ReadOnlySpan<Matrix4x4> palette));
        Assert.Equal(At(4f), model);
        Assert.Equal(Palette(3, 1f), palette.ToArray());
    }

    [Fact]
    public void Rigid_and_skinned_keys_are_separate_so_one_of_each_is_no_collision()
    {
        var history = new MotionHistory();
        history.BeginFrame();
        history.RecordRigid(A, At(1f));
        history.RecordSkinned(A, At(2f), Palette(1, 0f));
        Assert.Equal(0, history.Collisions);

        history.BeginFrame();
        Assert.True(history.TryGetPreviousRigid(A, out Matrix4x4 rigid));
        Assert.True(history.TryGetPreviousSkinned(A, out Matrix4x4 model, out _));
        Assert.Equal(At(1f), rigid);
        Assert.Equal(At(2f), model);
    }

    [Fact]
    public void None_is_never_recorded_and_never_found()
    {
        var history = new MotionHistory();
        history.BeginFrame();
        history.RecordRigid(MotionKey.None, At(1f));
        history.RecordSkinned(MotionKey.None, At(1f), Palette(2, 0f));
        Assert.Equal(0, history.KeyedRigid);
        Assert.Equal(0, history.KeyedSkinned);

        history.BeginFrame();
        Assert.False(history.TryGetPreviousRigid(MotionKey.None, out _));
        Assert.False(history.TryGetPreviousSkinned(MotionKey.None, out _, out ReadOnlySpan<Matrix4x4> palette));
        Assert.True(palette.IsEmpty);
    }

    [Fact]
    public void Last_frames_palettes_survive_this_frames_recording()
    {
        var history = new MotionHistory();
        history.BeginFrame();
        history.RecordSkinned(A, At(1f), Palette(4, 0f));
        history.RecordSkinned(B, At(2f), Palette(2, 5f));
        history.BeginFrame();
        history.RecordSkinned(A, At(3f), Palette(4, 9f));   // writes the other generation, never the one read below

        Assert.True(history.TryGetPreviousSkinned(A, out _, out ReadOnlySpan<Matrix4x4> a));
        Assert.Equal(Palette(4, 0f), a.ToArray());
        Assert.True(history.TryGetPreviousSkinned(B, out _, out ReadOnlySpan<Matrix4x4> b));
        Assert.Equal(Palette(2, 5f), b.ToArray());
    }

    [Fact]
    public void The_counters_describe_this_frame_only()
    {
        var history = new MotionHistory();
        history.BeginFrame();
        history.RecordRigid(A, At(1f));
        history.RecordRigid(A, At(1f));
        history.RecordSkinned(B, At(1f), Palette(1, 0f));
        history.BeginFrame();
        Assert.Equal(0, history.KeyedRigid);
        Assert.Equal(0, history.KeyedSkinned);
        Assert.Equal(0, history.Collisions);
    }

    [Fact]
    public void Reset_forgets_both_frames()
    {
        var history = new MotionHistory();
        history.BeginFrame();
        history.RecordRigid(A, At(1f));
        history.BeginFrame();
        history.RecordRigid(A, At(2f));
        history.Reset();
        Assert.Equal(0, history.KeyedRigid);
        Assert.False(history.TryGetPreviousRigid(A, out _));

        history.BeginFrame();
        Assert.False(history.TryGetPreviousRigid(A, out _));
    }

    [Fact]
    public void A_steady_frame_of_keyed_rigid_and_skinned_draws_allocates_nothing()
    {
        var history = new MotionHistory();
        Matrix4x4[] palette = Palette(64, 0.5f);
        Matrix4x4 world = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        MotionKey props = MotionKey.From(100), bodies = MotionKey.From(200);

        void Frame()
        {
            history.BeginFrame();
            for (uint i = 0; i < 2_000; i++) history.RecordRigid(MotionKey.Combine(props, i), world);
            history.RecordRigid(MotionKey.Combine(props, 0), world);   // one collision a frame
            for (uint i = 0; i < 40; i++) history.RecordSkinned(MotionKey.Combine(bodies, i), world, palette);
            for (uint i = 0; i < 2_000; i++) history.TryGetPreviousRigid(MotionKey.Combine(props, i), out _);
            for (uint i = 0; i < 40; i++) history.TryGetPreviousSkinned(MotionKey.Combine(bodies, i), out _, out _);
        }

        for (int i = 0; i < 4; i++) Frame();   // both generations reach their steady capacity
        Assert.Equal(2_001, history.KeyedRigid);   // not vacuous: every record really landed
        Assert.Equal(1, history.Collisions);
        Assert.True(history.TryGetPreviousSkinned(MotionKey.Combine(bodies, 39), out _, out ReadOnlySpan<Matrix4x4> last));
        Assert.Equal(palette.Length, last.Length);

        // Retries once before failing (see AllocAssert.NoPerCallAllocation) to ride out an unrelated gen-0
        // collision from the rest of the process, per issue #284.
        AllocAssert.NoPerCallAllocation("20 steady frames of 2,000 rigid and 40 skinned keys", () =>
        {
            for (int i = 0; i < 20; i++) Frame();
        });
    }
}
