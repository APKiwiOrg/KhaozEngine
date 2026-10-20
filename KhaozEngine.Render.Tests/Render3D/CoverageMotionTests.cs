using System;
using System.Numerics;
using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Gpu;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("NativeDeviceLifecycle")]
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

    static Vector3[] UploadedPrevious(CoverageSceneFixture h) => h.FinalPositions(previous: true);

    static Vector3[] UploadedOrdinaryPrevious(CoverageSceneFixture h)
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
        if (gpu || fallback)
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
            Assert.Equal(expected, UploadedOrdinaryPrevious(h));
        }
    }

    [GpuFact]
    public void Reset_seeds_current_final_surface()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        Draw(h); h.Render();
        h.Scene.CameraCut();
        var next = Draw(h, weight: .5f); h.Render();
        Assert.Null(h.Scene.PreviousFrameView);
        Assert.Equal(h.FinalPositions(), UploadedPrevious(h));
    }

    [GpuFact]
    public void Alternating_camera_objects_never_reuse_a_different_views_surface()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        Draw(h); h.Render();
        var other = new IsoCamera3D();
        other.Frame(Vector3.Zero, new Vector3(4, 3, 4));
        h.Scene.CameraOverride = other;
        var next = Draw(h, weight: .5f); h.Render();
        Assert.Equal(h.FinalPositions(), UploadedPrevious(h));
        h.Scene.CameraOverride = null;
        next = Draw(h, weight: .25f); h.Render();
        Assert.Equal(h.FinalPositions(), UploadedPrevious(h));
        Vector3[] prior = h.FinalPositions();
        Draw(h, weight: .75f); h.Render();
        Assert.Equal(prior, UploadedPrevious(h));
        Assert.Throws<System.InvalidOperationException>(() => h.Render());
    }

    [GpuFact]
    public void Equipment_identity_change_seeds_the_actual_current_final_upload()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        Draw(h); h.Render();
        var next = Draw(h, instance: 2, weight: .5f); h.Render();
        Assert.Equal(h.FinalPositions(), UploadedPrevious(h));
    }

    [GpuFact]
    public void Failed_encoding_and_skipped_frames_cannot_commit_staged_candidates()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        Draw(h); h.Render();
        var failed = Draw(h, weight: .5f);
        h.BeforeDraw = _ => throw new System.InvalidOperationException("synthetic encoding failure");
        Assert.Throws<System.InvalidOperationException>(() => h.Render());
        Assert.False(failed.Preparation!.MotionPresented);
        h.BeforeDraw = null;
        var next = Draw(h, weight: .25f); h.Render();
        Assert.Equal(h.FinalPositions(), UploadedPrevious(h));
        h.Begin();
        next = Draw(h, weight: .75f); h.Render();
        Assert.Equal(h.FinalPositions(), UploadedPrevious(h));
    }

    [GpuFact]
    public void Missing_frame_and_shadow_only_presentation_cannot_supply_previous_final_positions()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        Draw(h); h.Render();
        h.Begin(); h.Render();
        var next = Draw(h, weight: .5f); h.Render();
        Assert.Equal(h.FinalPositions(), UploadedPrevious(h));
        h.Begin();
        var shadow = h.Stage(weight: .25f);
        h.Scene.DrawSkinned(h.HostDraw(shadow, shadowOnly: true), h.Palette);
        h.SubmitEquipment(); h.Render();
        next = Draw(h, weight: .75f); h.Render();
        Assert.Equal(h.FinalPositions(), UploadedPrevious(h));
    }

    [GpuFact]
    public void Staging_refusal_cannot_overwrite_committed_surface_storage()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        var first = Draw(h); h.Render();
        Vector3[] last = h.FinalPositions();
        h.Begin();
        h.Stage(weight: .5f);
        Assert.False(h.Conformer.TryPrepare(h.Palette, Matrix4x4.Identity, h.CommandPoses,
            MotionKey.None, CoverageFallback.UncoveredCurrentPose, out _, out _));
        // A discarded Begin is a history gap even though it never reached the renderer.
        var next = Draw(h, weight: .25f); h.Render();
        Assert.NotEqual(last, UploadedPrevious(h));
        Assert.Equal(h.FinalPositions(), UploadedPrevious(h));
    }

    [GpuFact]
    public void Failed_geometry_overwrite_then_retry_preserves_the_committed_final_surface()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        var first = Draw(h); h.Render();
        Vector3[] last = h.FinalPositions();
        h.Begin();
        var frozen = new RigidInstanceDraw[1];
        Assert.True(h.Scene.TryStageCoverageEquipment(MotionKey.From(10), 1, 1, h.Support,
            [new RigidInstanceDraw(h.RigidHandle, Matrix4x4.Identity)], frozen, out var failure), failure.ToString());
        var refused = h.CommandPoses.Select(c => c with { RegionIndex = 31 }).ToArray();
        Assert.False(h.Conformer.TryPrepare(h.Palette, Matrix4x4.Identity, refused, MotionKey.From(10),
            CoverageFallback.UncoveredCurrentPose, out _, out failure));
        Assert.NotEqual(CoverageFailureCode.None, failure.Code);
        var valid = h.CommandPoses.Select(c => c with { TransitionWeight = .5f }).ToArray();
        Assert.True(h.Conformer.TryPrepare(h.Palette, Matrix4x4.Identity, valid, MotionKey.From(10),
            CoverageFallback.UncoveredCurrentPose, out var next, out failure), failure.ToString());
        h.Scene.DrawSkinned(h.HostDraw(next), h.Palette);
        h.Scene.Draw(frozen[0]); h.Render();
        Assert.Equal(last, UploadedPrevious(h));
        Assert.NotEqual(h.FinalPositions(), UploadedPrevious(h));
    }

    [GpuFact]
    public void Uploaded_motion_keeps_final_deformation_and_prior_model_in_current_origin()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        h.Scene.RenderOrigin = new(128, 0, 0);
        h.Begin();
        var first = h.Stage(Matrix4x4.CreateTranslation(1, 0, 0));
        h.Scene.DrawSkinned(h.HostDraw(first), h.Palette);
        h.SubmitEquipment();
        h.Render();
        Assert.Equal(CoveragePresentation.Dispatched, h.Scene.GetCoverageOutcome(first).Presentation);
        Vector3[] last = h.FinalPositions().Select(v => v + new Vector3(1 - 256, 0, 0)).ToArray();
        h.Scene.RenderOrigin = new(256, 0, 0);
        h.Begin();
        var next = h.Stage(Matrix4x4.CreateTranslation(2, 0, 0), weight: .5f);
        h.Scene.DrawSkinned(h.HostDraw(next), h.Palette);
        h.SubmitEquipment();
        h.Render();
        Assert.NotNull(h.Scene.PreviousFrameView);
        Assert.Equal(last, UploadedPrevious(h));
        Assert.All(h.NativeDraws, draw => Assert.Equal(new Vector3(-255, 0, 0), draw.PreviousModel.Translation));
    }

    static CoverageMotionHistory.Surface Surface(MotionKey key, ulong revision, Matrix4x4? model = null, bool presented = true) =>
        new(key, revision, new(0, 0, 0, 0, 0, 0, 3, 3, 1), model ?? Matrix4x4.Identity,
            new(new(-1, -1, -1), new(1, 1, 1)), presented);

    [Fact]
    public void Motion_uses_previous_final_vertices_after_rebase()
    {
        var history = new CoverageMotionHistory(); history.Reserve(2);
        object view = new(); var key = MotionKey.From(7);
        history.Begin(1, view, true);
        history.Record(Surface(key, 9, Matrix4x4.CreateTranslation(256, 5, 0))); history.Commit();
        history.Begin(2, view, true);
        Assert.True(history.TryGetPrevious(key, 9, out var previous));
        Assert.True(previous.TryRebase(new(384, 0, 0), Matrix4x4.Identity, out var model));
        Assert.Equal(new Vector3(-127, 7, 3), Vector3.Transform(new(1, 2, 3), model));
        Assert.Equal(new Vector3(-126, 9, 6), Vector3.Transform(new(2, 4, 6), model));
    }

    [Fact]
    public void Invalid_actual_prior_positions_models_and_view_inputs_refuse_reuse()
    {
        var key = MotionKey.From(7);
        Assert.False(Surface(key, 1, Matrix4x4.CreateTranslation(float.NaN, 0, 0)).TryRebase(Vector3.Zero, Matrix4x4.Identity, out _));
        Assert.False(Surface(key, 1, Matrix4x4.CreateScale(-1, 1, 1)).TryRebase(Vector3.Zero, Matrix4x4.Identity, out _));
        Assert.False(Surface(key, 1, Matrix4x4.CreateScale(0)).TryRebase(Vector3.Zero, Matrix4x4.Identity, out _));
        Assert.False(Surface(key, 1).TryRebase(Vector3.Zero, default, out _));
        Assert.False(Surface(key, 1).TryRebase(new(float.PositiveInfinity), Matrix4x4.Identity, out _));
    }

    [Fact]
    public void Only_committed_consecutive_same_view_surfaces_are_reusable()
    {
        var h = new CoverageMotionHistory(); h.Reserve(1);
        object view = new(), otherView = new(); var key = MotionKey.From(7);
        h.Begin(1, view, true); h.Record(Surface(key, 2));
        h.Begin(2, view, true); Assert.False(h.TryGetPrevious(key, 2, out _));
        h.Record(Surface(key, 2)); h.Commit();
        h.Begin(3, otherView, true); Assert.False(h.TryGetPrevious(key, 2, out _));
        h.Begin(4, view, true); Assert.False(h.TryGetPrevious(key, 2, out _));
        h.Record(Surface(key, 3)); h.Commit();
        h.Begin(5, view, true); Assert.False(h.TryGetPrevious(key, 2, out _));
        Assert.True(h.TryGetPrevious(key, 3, out _)); h.Commit();
        h.Begin(6, view, true); Assert.False(h.TryGetPrevious(key, 3, out _));
        h.Record(Surface(key, 3, presented: false)); h.Commit();
        h.Begin(7, view, true); Assert.False(h.TryGetPrevious(key, 3, out _));
    }

    [Fact]
    public void Warm_history_copy_lookup_and_commit_allocate_zero_bytes()
    {
        var h = new CoverageMotionHistory(); h.Reserve(2);
        object view = new(); var key = MotionKey.From(8); var surface = Surface(key, 4);
        h.Begin(1, view, true); h.Record(surface); h.Commit();
        h.Begin(2, view, true); h.Record(surface); h.Commit();
        using var region = AllocationRegion.Start("warm history copy, lookup and commit");
        long before = GC.GetAllocatedBytesForCurrentThread();
        h.Begin(3, view, true); bool found = h.TryGetPrevious(key, 4, out var previous);
        h.Record(surface); h.Commit();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        region.Dispose();
        Assert.True(found); Assert.Equal(surface, previous); Assert.Equal(0, allocated);
    }

    [GpuFact]
    public void Replacing_host_topology_seeds_the_new_incarnation()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        Draw(h); h.Render();
        using var replacement = h.Scene.CreateHostConformer(h.HostMesh.Skeleton!, h.Host);
        replacement.ReserveSupports([h.Support]);
        h.Begin(); h.Stage();
        var commands = h.CommandPoses.Select(c => c with { Topology = replacement.Topology, TransitionWeight = .5f }).ToArray();
        Assert.True(replacement.TryPrepare(h.Palette, Matrix4x4.Identity, commands, MotionKey.From(10),
            CoverageFallback.UncoveredCurrentPose, out var next, out var failure), failure.ToString());
        h.LastCoverageFrame = next;
        h.Scene.DrawSkinned(h.HostDraw(next), h.Palette);
        h.SubmitEquipment(); h.Render();
        Assert.Equal(h.FinalPositions(), UploadedPrevious(h));
    }
}
