using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class PointShadowAttributionWorkTests
{
    [Fact]
    public void Duplicate_static_requests_observe_dirt_at_each_comparison_and_select_after_all_comparisons()
    {
        using var f = new ShadowStageProbeFixture();
        var capture = new PointShadowAttributionCapture(Guid.NewGuid(), new(8, 8, 64, 64, 128, 64, 32, 32));
        float first = 0, second = 0;
        void Queue(Scene3D s)
        {
            s.AddLight(new(first, 2, 0), Color.White, 6, 1, LightShadow.Static(7));
            s.AddLight(new(second, 2, 0), Color.White, 6, 1, LightShadow.Static(7));
        }
        f.Render(ShadowStageProbeMode.Off, staticLight: false, dynamicLight: false, extraQueue: Queue);
        f.Render(ShadowStageProbeMode.Off, staticLight: false, dynamicLight: false, extraQueue: Queue);
        first = second = .001f;
        var shared = f.Render(ShadowStageProbeMode.Counts, staticLight: false, dynamicLight: false, extraQueue: Queue, attribution: capture);
        var one = capture.GetRow(shared.PointAttribution!.Value.RowStart);
        var two = capture.GetRow(shared.PointAttribution.Value.RowStart + 1);
        Assert.True(one.Mismatch); Assert.False(one.PriorDirty);
        Assert.False(two.Mismatch); Assert.True(two.PriorDirty); Assert.True(two.DirtyAfter);
        second = .002f;
        var changed = f.Render(ShadowStageProbeMode.Counts, staticLight: false, dynamicLight: false, extraQueue: Queue, attribution: capture);
        var before = capture.GetRow(changed.PointAttribution!.Value.RowStart);
        var later = capture.GetRow(changed.PointAttribution.Value.RowStart + 1);
        Assert.False(before.DirtyAfter); Assert.Equal(0, before.SelectedOrdinal); Assert.True(before.Completed);
        Assert.True(later.DirtyAfter); Assert.Equal(before.Slot, later.Slot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Static_transient_and_dynamic_skinned_work_has_the_actual_request_and_packed_order(bool gpu)
    {
        using var f = new ShadowStageProbeFixture();
        f.Scene.UseGpuSkinning = gpu;
        var tube = SkinnedMeshBuilder.BuildTube(.35f, 2, 4, 6, 3, Axis.Z);
        var mesh = f.Scene.LoadSkinnedMesh(tube);
        var capture = new PointShadowAttributionCapture(Guid.NewGuid(), new(8, 8, 64, 64, 128, 64, 32, 32));
        void Queue(Scene3D s) => s.DrawSkinned(mesh, tube.RestPose, Matrix4x4.Identity, Color.White);
        f.Render(ShadowStageProbeMode.Counts, extraQueue: Queue, attribution: capture);
        f.Render(ShadowStageProbeMode.Counts, extraQueue: Queue, attribution: capture);
        var snapshot = f.Render(ShadowStageProbeMode.Counts, extraQueue: Queue, attribution: capture);
        var frame = snapshot.PointAttribution!.Value;
        var rows = Enumerable.Range(frame.RowStart, frame.RowCount).Select(capture.GetRow).ToArray();
        Assert.True(rows[0].TransientCompleted); Assert.True(rows[0].TransientSkinnedDraws > 0);
        Assert.False(rows[0].Completed); Assert.Equal(-1, rows[0].SelectedOrdinal);
        Assert.True(rows[1].Completed); Assert.True(rows[1].DynamicSkinnedDraws > 0);
        Assert.Equal(PointAttributionSignatureStatus.NotApplicable, rows[1].SignatureStatus);
        Assert.Equal(new[] { 0, 1 }, new[] { rows[1].SelectedOrdinal, rows[0].TransientSelectedOrdinal });
        var counts = snapshot.Counters!.Value;
        Assert.Equal(counts.PointFaceDrawCalls, rows.Sum(r => r.RigidDraws + r.FoliageDraws + r.DynamicSkinnedDraws + r.TransientSkinnedDraws));
        Assert.Equal(counts.PointTransientSkinnedFaceDrawCalls, rows.Sum(r => r.TransientSkinnedDraws));
        Assert.Equal(counts.PointDynamicSkinnedFaceDrawCalls, rows.Sum(r => r.DynamicSkinnedDraws));
    }

    [Fact]
    public void Overflow_and_cancellation_preserve_render_work_and_old_ranges()
    {
        using var f = new ShadowStageProbeFixture(); using var control = new ShadowStageProbeFixture();
        var capture = new PointShadowAttributionCapture(Guid.NewGuid(), new(8, 1, 8, 8, 8, 8, 4, 8));
        var first = f.Render(ShadowStageProbeMode.Counts, attribution: capture);
        control.Render(ShadowStageProbeMode.Counts);
        Assert.Equal(PointAttributionCapacity.Requests, capture.Overflow);
        Assert.False(first.PointAttribution!.Value.Complete);
        f.Render(ShadowStageProbeMode.Counts, attribution: capture); control.Render(ShadowStageProbeMode.Counts);
        Assert.Equal(f.Order(), control.Order()); Assert.Equal(f.Signatures(), control.Signatures());
        var isolated = new PointShadowAttributionCapture(Guid.NewGuid(), new(4, 4, 8, 8, 16, 8, 4, 8));
        var request = new ShadowStageProbeRequest(isolated.Epoch, 1, ShadowStageProbeMode.Counts, isolated);
        f.Scene.ConfigureShadowStageProbe(request);
        f.Scene.PreparePointShadowRequests(Vector3.Zero); f.Scene.CancelShadowStageProbe();
        Assert.Equal(PointAttributionAvailability.Abandoned, isolated.GetFrame(0).Availability);
        Assert.False(isolated.GetFrame(0).Complete);
        isolated.Freeze();
    }
}
