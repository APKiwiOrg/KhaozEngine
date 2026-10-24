using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfGeometryTraceTests;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfPointTraceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frozen_point_layout_survives_opposite_dirty_subsets_with_one_row_budget(bool reverseRequestOrder)
    {
        using var rig = new GeometryRig(true, secondStaticRow: true);
        rig.Frame(); rig.Frame(); rig.Frame();
        if (reverseRequestOrder) rig.Frame(rigidOffset: .1f);
        rig.Scene.Post.Quality.Shadows.PointShadows.MaxStaticRebuildsPerFrame = 2;
        var observer = new GeometryObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        rig.Frame(rigidOffset: .2f, secondRigidOffset: .2f);
        observer.AssertClosed();
        var definitions = observer.Freeze();
        Assert.Equal(2, definitions.Count(p => p.Id == "shadow.point.static"));
        Assert.Equal(reverseRequestOrder ? new[] { 1, 0 } : new[] { 0, 1 },
            definitions.Where(p => p.Id == "shadow.point.static").Select(p => p.Occurrence));
        Assert.Equal(12, definitions.Count(p => p.Id == "shadow.point.static.face"));
        Assert.Equal(12, definitions.Count(p => p.Id == "shadow.point.static.rigid"));
        rig.Scene.Post.Quality.Shadows.PointShadows.MaxStaticRebuildsPerFrame = 1;
        var recorder = new RendererPerfRecorder(2, RendererPerfLayout.PerPass, definitions, () => 0, 1000);
        var adapter = new RendererPerfPassObserver(recorder, RendererPerfLayout.PerPass, definitions);
        rig.Scene.PassObserver = adapter;
        var traced = RendererPerfCommandList.Wrap(rig.Commands, recorder);
        CheckFrame(0, reverseRequestOrder ? .2f : .4f, reverseRequestOrder ? .4f : .2f,
            cachedRow: reverseRequestOrder ? 0 : 1);
        CheckFrame(1, .4f, .4f, cachedRow: reverseRequestOrder ? 1 : 0);

        void CheckFrame(int frame, float first, float second, int cachedRow)
        {
            recorder.BeginFrame(frame, frame, 0);
            adapter.BeginFrame();
            rig.Frame(rigidOffset: first, secondRigidOffset: second, observedCommands: traced);
            recorder.EndFrame();
            Assert.Equal(1, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);
            var sample = recorder.Harvest(frame);
            Assert.Empty(sample.Errors);
            Assert.Equal(definitions.Select(p => (p.Id, p.Occurrence)), sample.Passes.Select(p => (p.Id, p.Occurrence)));
            foreach (var definition in definitions.Where(p => p.Id.StartsWith("shadow.point.static", StringComparison.Ordinal)))
            {
                int row = definition.Id is "shadow.point.static" or "shadow.point.static.setup"
                    ? definition.Occurrence : definition.Occurrence / 6;
                var pass = Assert.Single(sample.Passes, p => p.Id == definition.Id && p.Occurrence == definition.Occurrence);
                if (row == cachedRow) Assert.Equal(RendererPerfMetricStatus.NotExecuted, pass.Duration.Status);
            }
            Assert.NotEqual(RendererPerfMetricStatus.NotExecuted,
                Assert.Single(sample.Passes, p => p.Id == "shadow.point.static" && p.Occurrence != cachedRow).Duration.Status);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Null_observer_does_not_read_point_profiling_metadata(bool gpu)
    {
        using var rig = new GeometryRig(gpu);
        rig.Frame(); rig.Frame(); rig.Frame();
        var field = typeof(Scene3D).GetField("_packedPointSlots", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var slots = Assert.IsAssignableFrom<IList>(field.GetValue(rig.Scene));
        Assert.NotEmpty(slots);
        for (int i = 0; i < slots.Count; i++)
        {
            object packed = slots[i]!;
            // Rigid recording needs no request lookup. Only profiling would inspect this invalid index.
            packed.GetType().GetProperty("RequestIndex")!.SetValue(packed, int.MaxValue);
            var casters = packed.GetType().GetProperty("Casters")!;
            casters.SetValue(packed, Enum.ToObject(casters.PropertyType, 1));
            slots[i] = packed;
        }
        rig.Commands.Events.Clear();
        Assert.Null(rig.Scene.PassObserver);
        Assert.Null(Record.Exception(() => rig.Scene.RenderPointShadowSlots(rig.Commands)));
        Assert.Contains(rig.Commands.Events, e => e.Op == "draw-indexed");
        // Positive control. The same metadata must be inspected when profiling is enabled.
        rig.Scene.PassObserver = new GeometryObserver(rig.Commands);
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Scene.RenderPointShadowSlots(rig.Commands));
    }
}
