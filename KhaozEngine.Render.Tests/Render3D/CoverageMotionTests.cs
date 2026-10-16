using System;
using System.Numerics;
using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Gpu;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageMotionTests
{
    static SkinnedCoverageFrame Draw(CoverageSceneFixture h, ulong instance = 1, float weight = 1)
    {
        h.Begin();
        var frame = h.Stage(instance: instance, weight: weight);
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.SubmitEquipment();
        return frame;
    }

    static Vector3[] UploadedPrevious(CoverageSceneFixture h)
    {
        var buffer = h.Scene.CoverageMotionReserveForTests.Buffer;
        return MemoryMarshal.Cast<byte, Vector3>(Assert.Single(h.Record.Uploads,
            u => ReferenceEquals(u.Buffer, buffer)).Data).ToArray();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Restored_ordinary_surface_seeds_current_cpu_or_gpu_inputs(bool gpu, bool fallback)
    {
        using var h = new CoverageSceneFixture();
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        h.Scene.UseGpuSkinning = gpu;
        h.Scene.RenderOrigin = new(128, 0, 0);
        h.Begin();
        var first = h.Stage(Matrix4x4.CreateTranslation(1, 0, 0));
        h.Scene.DrawSkinned(h.HostDraw(first), h.Palette); h.SubmitEquipment(); h.Render();
        h.Scene.RenderOrigin = new(256, 0, 0);
        h.Begin();
        Matrix4x4 world = Matrix4x4.CreateTranslation(2, 0, 0);
        var next = fallback ? h.Stage(world) : default;
        h.Scene.DrawSkinned(new SkinnedInstanceDraw(h.Host[0], world)
        { Coverage = next, Motion = MotionKey.From(10) }, h.Palette);
        h.Render();
        if (fallback) Assert.Equal(CoveragePresentation.UncoveredFallback, h.Scene.GetCoverageOutcome(next).Presentation);
        if (gpu)
        {
            var resources = h.Scene.MotionResourcesForTests!;
            var range = (GpuBufferRange)Assert.IsType<FakeResourceSet>(resources.SkinnedPalette.Set).Resources[1];
            var image = Assert.Single(h.Record.Uploads, u => ReferenceEquals(u.Buffer, range.Buffer)).Data!;
            Assert.Equal(Matrix4x4.CreateTranslation(-254, 0, 0), MemoryMarshal.Read<Matrix4x4>(image));
            Assert.Equal(h.Palette, MemoryMarshal.Cast<byte, Matrix4x4>(image.AsSpan(64, h.Palette.Length * 64)).ToArray());
            Assert.Contains(h.Record.Bindings, b => b.Sets.Values.Any(s => ReferenceEquals(s.Set, resources.SkinnedPalette.Set)));
        }
        else
        {
            var previousBuffer = h.Scene.CoverageMotionReserveForTests.Buffer;
            var draw = h.Record.Bindings.First(b => b.VertexBuffers.TryGetValue(2, out var bound) && ReferenceEquals(bound, previousBuffer));
            var current = Assert.Single(h.Record.Uploads, u => ReferenceEquals(u.Buffer, draw.VertexBuffers[0]));
            var expected = MemoryMarshal.Cast<byte, ModelVertex>(current.Data).ToArray()
                .Select(v => v.Position + new Vector3(-254, 0, 0)).ToArray();
            Assert.Equal(expected, UploadedPrevious(h));
        }
    }

    [Fact]
    public void Reset_seeds_current_final_surface()
    {
        using var h = new CoverageSceneFixture();
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        Draw(h); h.Render();
        h.Scene.CameraCut();
        var next = Draw(h, weight: .5f); h.Render();
        Assert.Null(h.Scene.PreviousFrameView);
        Assert.Equal(h.Scene.CoverageVerticesForTests(next).ToArray().Select(v => v.Position), UploadedPrevious(h));
    }

    [Fact]
    public void Alternating_camera_objects_never_reuse_a_different_views_surface()
    {
        using var h = new CoverageSceneFixture();
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        Draw(h); h.Render();
        var other = new IsoCamera3D();
        other.Frame(Vector3.Zero, new Vector3(4, 3, 4));
        h.Scene.CameraOverride = other;
        var next = Draw(h, weight: .5f); h.Render();
        Assert.Equal(h.Scene.CoverageVerticesForTests(next).ToArray().Select(v => v.Position), UploadedPrevious(h));
        h.Scene.CameraOverride = null;
        next = Draw(h, weight: .25f); h.Render();
        Assert.Equal(h.Scene.CoverageVerticesForTests(next).ToArray().Select(v => v.Position), UploadedPrevious(h));
        Vector3[] prior = h.Scene.CoverageVerticesForTests(next).ToArray().Select(v => v.Position).ToArray();
        Draw(h, weight: .75f); h.Render();
        Assert.Equal(prior, UploadedPrevious(h));
        Assert.Throws<System.InvalidOperationException>(() => h.Render());
    }

    [Fact]
    public void Equipment_identity_change_seeds_the_actual_current_final_upload()
    {
        using var h = new CoverageSceneFixture();
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        Draw(h); h.Render();
        var next = Draw(h, instance: 2, weight: .5f); h.Render();
        Assert.Equal(h.Scene.CoverageVerticesForTests(next).ToArray().Select(v => v.Position), UploadedPrevious(h));
    }

    [Fact]
    public void Failed_encoding_and_skipped_frames_cannot_commit_staged_candidates()
    {
        using var h = new CoverageSceneFixture();
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        Draw(h); h.Render();
        Draw(h, weight: .5f);
        h.BeforeDraw = _ => throw new System.InvalidOperationException("synthetic encoding failure");
        Assert.Throws<System.InvalidOperationException>(() => h.Render());
        h.BeforeDraw = null;
        var next = Draw(h, weight: .25f); h.Render();
        Assert.Equal(h.Scene.CoverageVerticesForTests(next).ToArray().Select(v => v.Position), UploadedPrevious(h));
        h.Begin();
        next = Draw(h, weight: .75f); h.Render();
        Assert.Equal(h.Scene.CoverageVerticesForTests(next).ToArray().Select(v => v.Position), UploadedPrevious(h));
    }

    [Fact]
    public void Missing_frame_and_shadow_only_presentation_cannot_supply_previous_final_positions()
    {
        using var h = new CoverageSceneFixture();
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        Draw(h); h.Render();
        h.Begin(); h.Render();
        var next = Draw(h, weight: .5f); h.Render();
        Assert.Equal(h.Scene.CoverageVerticesForTests(next).ToArray().Select(v => v.Position), UploadedPrevious(h));
        h.Begin();
        var shadow = h.Stage(weight: .25f);
        h.Scene.DrawSkinned(h.HostDraw(shadow, shadowOnly: true), h.Palette);
        h.SubmitEquipment(); h.Render();
        next = Draw(h, weight: .75f); h.Render();
        Assert.Equal(h.Scene.CoverageVerticesForTests(next).ToArray().Select(v => v.Position), UploadedPrevious(h));
    }

    [Fact]
    public void Staging_refusal_cannot_overwrite_committed_surface_storage()
    {
        using var h = new CoverageSceneFixture();
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        var first = Draw(h); h.Render();
        Vector3[] last = h.Scene.CoverageVerticesForTests(first).ToArray().Select(v => v.Position).ToArray();
        h.Begin();
        h.Stage(weight: .5f);
        Assert.False(h.Conformer.TryPrepare(h.Palette, Matrix4x4.Identity, h.CommandPoses,
            MotionKey.None, CoverageFallback.UncoveredCurrentPose, out _, out _));
        // A discarded Begin is a history gap even though it never reached the renderer.
        var next = Draw(h, weight: .25f); h.Render();
        Assert.NotEqual(last, UploadedPrevious(h));
        Assert.Equal(h.Scene.CoverageVerticesForTests(next).ToArray().Select(v => v.Position), UploadedPrevious(h));
    }

    [Fact]
    public void Failed_geometry_overwrite_then_retry_preserves_the_committed_final_surface()
    {
        using var h = new CoverageSceneFixture();
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        var first = Draw(h); h.Render();
        Vector3[] last = h.Scene.CoverageVerticesForTests(first).ToArray().Select(v => v.Position).ToArray();
        h.Begin();
        var frozen = new RigidInstanceDraw[1];
        Assert.True(h.Scene.TryStageCoverageEquipment(MotionKey.From(10), 1, 1, h.Support,
            [new RigidInstanceDraw(h.RigidHandle, Matrix4x4.Identity)], frozen, out var failure), failure.ToString());
        var refused = h.CommandPoses.Select(c => c with { MaxWorldRecession = 0 }).ToArray();
        Assert.False(h.Conformer.TryPrepare(h.Palette, Matrix4x4.Identity, refused, MotionKey.From(10),
            CoverageFallback.UncoveredCurrentPose, out _, out failure));
        Assert.NotEqual(CoverageFailureCode.None, failure.Code);
        var valid = h.CommandPoses.Select(c => c with { TransitionWeight = .5f }).ToArray();
        Assert.True(h.Conformer.TryPrepare(h.Palette, Matrix4x4.Identity, valid, MotionKey.From(10),
            CoverageFallback.UncoveredCurrentPose, out var next, out failure), failure.ToString());
        h.Scene.DrawSkinned(h.HostDraw(next), h.Palette);
        h.Scene.Draw(frozen[0]); h.Render();
        Assert.Equal(last, UploadedPrevious(h));
        Assert.NotEqual(h.Scene.CoverageVerticesForTests(next).ToArray().Select(v => v.Position).ToArray(), UploadedPrevious(h));
    }

    [Fact]
    public void Uploaded_motion_keeps_final_deformation_and_prior_model_in_current_origin()
    {
        using var h = new CoverageSceneFixture();
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        h.Scene.RenderOrigin = new(128, 0, 0);
        h.Begin();
        var first = h.Stage(Matrix4x4.CreateTranslation(1, 0, 0));
        h.Scene.DrawSkinned(h.HostDraw(first), h.Palette);
        h.SubmitEquipment();
        h.Render();
        Assert.Equal(CoveragePresentation.Covered, h.Scene.GetCoverageOutcome(first).Presentation);
        Vector3[] last = h.Scene.CoverageVerticesForTests(first).ToArray().Select(v => v.Position + new Vector3(1 - 256, 0, 0)).ToArray();
        h.Scene.RenderOrigin = new(256, 0, 0);
        h.Begin();
        var next = h.Stage(Matrix4x4.CreateTranslation(2, 0, 0), weight: .5f);
        h.Scene.DrawSkinned(h.HostDraw(next), h.Palette);
        h.SubmitEquipment();
        h.Render();
        Assert.NotNull(h.Scene.PreviousFrameView);
        var previousBuffer = h.Scene.CoverageMotionReserveForTests.Buffer;
        var upload = Assert.Single(h.Record.Uploads, u => ReferenceEquals(u.Buffer, previousBuffer));
        Assert.Equal(last, MemoryMarshal.Cast<byte, Vector3>(upload.Data).ToArray());
    }

    [Fact]
    public void Motion_uses_previous_final_vertices_after_rebase()
    {
        var history = new CoverageMotionHistory();
        history.Reserve(2, 8);
        object view = new();
        var key = MotionKey.From(7);
        Vector3[] final = [new(1, 2, 3), new(2, 4, 6), new(3, 6, 9)];
        history.Begin(1, view, true);
        history.Record(key, 9, final, Matrix4x4.CreateTranslation(256, 5, 0));
        history.Commit();
        final[0] = new(999);
        history.Begin(2, view, true);
        Assert.True(history.TryGetPrevious(key, 9, out var previous));
        Assert.Equal(3, previous.Positions.Length);
        Assert.True(previous.TryRebase(new Vector3(384, 0, 0), Matrix4x4.Identity, out var model));
        Assert.Equal(new Vector3(-127, 7, 3), Vector3.Transform(previous.Positions[0], model));
        Assert.Equal(new Vector3(-126, 9, 6), Vector3.Transform(previous.Positions[1], model));
    }

    [Fact]
    public void Invalid_actual_prior_positions_models_and_view_inputs_refuse_reuse()
    {
        var nonfinite = new CoveragePreviousSurface([new(float.NaN, 0, 0)], Matrix4x4.Identity);
        Assert.False(nonfinite.TryRebase(Vector3.Zero, Matrix4x4.Identity, out _));
        var mirror = new CoveragePreviousSurface([Vector3.One], Matrix4x4.CreateScale(-1, 1, 1));
        Assert.False(mirror.TryRebase(Vector3.Zero, Matrix4x4.Identity, out _));
        var singular = new CoveragePreviousSurface([Vector3.One], Matrix4x4.CreateScale(0));
        Assert.False(singular.TryRebase(Vector3.Zero, Matrix4x4.Identity, out _));
        var valid = new CoveragePreviousSurface([Vector3.One], Matrix4x4.Identity);
        Assert.False(valid.TryRebase(Vector3.Zero, default, out _));
        Assert.False(valid.TryRebase(new(float.PositiveInfinity), Matrix4x4.Identity, out _));
    }

    [Fact]
    public void Only_committed_consecutive_same_view_surfaces_are_reusable()
    {
        var h = new CoverageMotionHistory();
        h.Reserve(1, 4);
        object view = new(), otherView = new();
        var key = MotionKey.From(7);
        h.Begin(1, view, true);
        h.Record(key, 2, [Vector3.One], Matrix4x4.Identity);
        h.Begin(2, view, true);
        Assert.False(h.TryGetPrevious(key, 2, out _));
        h.Record(key, 2, [new(2)], Matrix4x4.Identity);
        h.Commit();
        h.Begin(3, otherView, true);
        Assert.False(h.TryGetPrevious(key, 2, out _));
        h.Begin(4, view, true);
        Assert.False(h.TryGetPrevious(key, 2, out _));
        h.Record(key, 3, [new(3)], Matrix4x4.Identity);
        h.Commit();
        h.Begin(5, view, true);
        Assert.False(h.TryGetPrevious(key, 2, out _));
        Assert.True(h.TryGetPrevious(key, 3, out var p));
        Assert.Equal(new Vector3(3), p.Positions[0]);
        h.Commit();
        h.Begin(6, view, true);
        Assert.False(h.TryGetPrevious(key, 3, out _));
    }

    [Fact]
    public void Warm_history_copy_lookup_and_commit_allocate_zero_bytes()
    {
        var h = new CoverageMotionHistory();
        h.Reserve(2, 8);
        object view = new();
        Vector3[] positions = [new(1, 2, 3), new(2, 4, 6)];
        var key = MotionKey.From(8);
        h.Begin(1, view, true); h.Record(key, 4, positions, Matrix4x4.Identity); h.Commit();
        h.Begin(2, view, true); h.Record(key, 4, positions, Matrix4x4.Identity); h.Commit();
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        h.Begin(3, view, true);
        bool found = h.TryGetPrevious(key, 4, out var previous);
        Vector3 witness = previous.Positions[0];
        h.Record(key, 4, positions, Matrix4x4.Identity); h.Commit();
        long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(found);
        Assert.Equal(new Vector3(1, 2, 3), witness);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Replacing_host_topology_seeds_the_new_incarnation()
    {
        using var h = new CoverageSceneFixture();
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        Draw(h); h.Render();
        using var replacement = h.Scene.CreateHostConformer(h.HostMesh.Skeleton!, h.Host);
        h.Begin(); h.Stage();
        var commands = h.CommandPoses.Select(c => c with { Topology = replacement.Topology, TransitionWeight = .5f }).ToArray();
        Assert.True(replacement.TryPrepare(h.Palette, Matrix4x4.Identity, commands, MotionKey.From(10),
            CoverageFallback.UncoveredCurrentPose, out var next, out var failure), failure.ToString());
        h.Scene.DrawSkinned(h.HostDraw(next), h.Palette);
        h.SubmitEquipment(); h.Render();
        Assert.Equal(h.Scene.CoverageVerticesForTests(next).ToArray().Select(v => v.Position), UploadedPrevious(h));
    }
}
