using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfPostTraceTests
{
    [Theory]
    [InlineData("effects-ui")]
    [InlineData("outdoors")]
    [InlineData("legacy")]
    [InlineData("msaa-blob")]
    [InlineData("split")]
    public void Attributes_every_post_copy_and_effect(string mode)
    {
        using var rig = new PostTraceRig(mode);
        rig.Frame(64); rig.Frame(65); rig.Frame(66); rig.Frame(67);
        Assert.Equal(0, rig.Scene.RetiredResourceCount);
        int discoveryWaits = rig.Device.Waits;
        var observer = new PostTraceObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        rig.Frame(68);
        Assert.Equal(discoveryWaits, rig.Device.Waits);
        Assert.Equal(0, rig.Scene.RetiredResourceCount);
        observer.AssertClosed();
        Assert.Contains(observer.Passes, p => p.Key.Id == "post.blit");
        Assert.All(rig.Commands.Events.Where(e => e.Op is not ("begin" or "end")), e =>
            Assert.True(e.Owner.HasValue, $"Unowned {e.Op}"));
        Assert.All(rig.Commands.Events.Where(e => e.Owner.HasValue), e =>
            Assert.DoesNotContain(observer.Passes, p => p.Parent == e.Owner));
        foreach (var (shader, owner) in new[]
        {
            (ShaderSources.BloomBrightFrag, "post.bloom.bright"),
            (ShaderSources.BloomCompositeFrag, "post.bloom.composite"),
            (ShaderSources.BlitFrag, "post.blit"),
            (ShaderSources.DistortionApplyFrag, "post.distortion-apply"),
            (ShaderSources.TemporalEdgeFrag, "temporal.outline-inputs"),
            (ShaderSources.TemporalDepthStoreFrag, "temporal.depth-store"),
            (ShaderSources.TargetOutlineFullMaskFrag, "outline.mask"),
            (ShaderSources.TargetOutlineVisibleMaskFrag, "outline.visible-mask"),
            (ShaderSources.TargetOutlineCompositeFrag, "outline.composite"),
        })
            Assert.All(rig.Commands.Events.Where(e => IsDraw(e, shader)), e => Assert.Equal(owner, e.Owner?.Id));
        Assert.Equal(new[] { "post.bloom.horizontal", "post.bloom.vertical" }, rig.Commands.Events
            .Where(e => IsDraw(e, ShaderSources.BloomBlurFrag)).Select(e => e.Owner?.Id));
        if (mode == "outdoors")
        {
            Assert.Equal(2, rig.Commands.Events.Count(e => e.Op == "dispatch"));
            Assert.Contains(observer.Passes, p => p.Key.Id == "water.surface");
            Assert.Equal(4, observer.Passes.Count(p => p.Key.Id == "ocean.mip-copy"));
            Assert.Single(observer.Passes, p => p.Key.Id == "ocean.mips");
        }
        else
        {
            foreach (string id in new[] { "effects.textured-billboards", "effects.beams", "effects.trails",
                "effects.overlay-meshes", "effects.silhouettes", "effects.decals", "effects.particles",
                "effects.distortion", "post.distortion-apply", "post.bloom.bright", "post.bloom.horizontal",
                "post.bloom.vertical", "post.bloom.composite", "display.fills", "display.lines",
                "display.billboards-additive", "display.billboards-alpha", "display.transition", "ui.sprites" })
                Assert.Contains(observer.Passes, p => p.Key.Id == id);
            Assert.Equal(4, observer.Passes.Count(p => p.Key.Id == "outline.composite"));
            if (mode == "legacy" || mode == "msaa-blob")
                foreach (string id in new[] { "post.quantize", "post.edge", "post.fxaa", "background.starfield" })
                    Assert.Contains(observer.Passes, p => p.Key.Id == id);
            else
                foreach (string id in new[] { "temporal.outline-inputs", "post.sharpen", "display.debug" }
                    .Concat(mode == "split" ? new[] { "temporal.prepare", "temporal.accumulate" }
                        : new[] { "temporal.resolve", "temporal.depth-store" }))
                    Assert.Contains(observer.Passes, p => p.Key.Id == id);
            if (mode != "legacy") Assert.Contains(observer.Passes, p => p.Key.Id == "post.tonemap");
            if (mode == "msaa-blob")
            {
                Assert.Contains(observer.Passes, p => p.Key.Id == "copy.blob-depth-resolve");
                Assert.Contains(observer.Passes, p => p.Key.Id == "effects.blob-decals");
                Assert.True(rig.Commands.Events.Count(e => e.Op == "resolve") >= 4);
            }
        }
        var definitions = observer.Freeze();
        Assert.InRange(definitions.Length, 1, 255);
        var recorder = new RendererPerfRecorder(1, RendererPerfLayout.PerPass, definitions, () => 0, 1000);
        var adapter = new RendererPerfPassObserver(recorder, RendererPerfLayout.PerPass, definitions);
        using var recorded = new PostTraceRig(mode, recorder);
        recorded.Frame(64); recorded.Frame(65); recorded.Frame(66); recorded.Frame(67); recorded.Frame(68);
        Assert.Equal(0, recorded.Scene.RetiredResourceCount);
        int measuredWaits = recorded.Device.Waits;
        recorded.Scene.PassObserver = adapter;
        recorder.BeginFrame(0, 0, 0); adapter.BeginFrame();
        recorded.Frame(69, RendererPerfCommandList.Wrap(recorded.Commands, recorder));
        recorder.EndFrame();
        Assert.Empty(recorder.Harvest(0).Errors);
        Assert.Equal(measuredWaits, recorded.Device.Waits);
        Assert.Equal(0, recorded.Scene.RetiredResourceCount);
        Assert.Equal(0, recorder.ReadCounter(0, RendererPerfCounter.WaitForIdleCalls));
        RendererPerfFrameBoundaryTraceTests.AssertLeafOwnership(recorder, definitions);
    }

    [Fact]
    public void Prime_is_startup_and_column_next_row_are_distinct()
    {
        using var rig = new PostTraceRig("outdoors");
        var observer = new PostTraceObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        rig.Frame(64);
        Assert.Single(observer.Passes, p => p.Key.Id == "startup.ocean-prime");
        var prime = Assert.Single(rig.Device.Lists, list => list.Events.Any(e => e.Owner?.Id == "startup.ocean-prime"));
        Assert.NotSame(rig.Commands, prime);
        Assert.Single(prime.Events, e => e.Op == "dispatch");
        Assert.All(prime.Events.Where(e => e.Op is not ("begin" or "end")), e =>
            Assert.Equal(new RenderPassKey("startup.ocean-prime"), e.Owner));
        Assert.Equal(1, rig.PreparationSubmits);
        int coldSubmits = rig.Device.Native.Submits;
        Assert.Equal(1, rig.Scene.LastWaterStats.OceanStalls);
        observer = new PostTraceObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        rig.Frame(65);
        Assert.DoesNotContain(observer.Passes, p => p.Key.Id == "startup.ocean-prime");
        Assert.Equal(new[] { "ocean.next-row", "ocean.column" }, rig.Commands.Events
            .Where(e => e.Op == "dispatch").Select(e => e.Owner?.Id));
        Assert.Equal(coldSubmits, rig.Device.Native.Submits);
        Assert.Equal(0, rig.PreparationSubmits);
        Assert.Equal(0, rig.Scene.LastWaterStats.OceanStalls);
        Assert.Equal(1, rig.Device.Native.PeakOpenLists);
    }

    [Theory]
    [InlineData("effects-ui")]
    [InlineData("split")]
    public void Fused_or_skipped_work_is_not_zero_duration(string mode)
    {
        using var rig = new PostTraceRig(mode);
        rig.Frame(64); rig.Frame(65);
        var observer = new PostTraceObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        rig.Frame(66);
        Assert.Contains(observer.Fused, key => key.Id == "opaque.gpu-skinned");
        Assert.Contains(observer.Skipped, key => key.Id == "copy.color-resolve");
        var definitions = observer.Freeze();
        var recorder = new RendererPerfRecorder(1, RendererPerfLayout.PerPass, definitions, () => 0, 1000);
        rig.Scene.PassObserver = new RendererPerfPassObserver(recorder, RendererPerfLayout.PerPass, definitions);
        rig.Scene.Post.Bloom.Enabled = false;
        recorder.BeginFrame(0, 0, 0);
        rig.Frame(67, RendererPerfCommandList.Wrap(rig.Commands, recorder));
        recorder.EndFrame();
        var sample = recorder.Harvest(0);
        Assert.Empty(sample.Errors);
        var copy = Assert.Single(sample.Passes, p => p.Id == "copy.color-resolve");
        Assert.Equal(RendererPerfMetricStatus.NotExecuted, copy.Duration.Status);
        Assert.Null(copy.Duration.Value);
        Assert.DoesNotContain(sample.Passes, p => p.Id == "motion.fused");
        Assert.DoesNotContain(sample.Passes, p => p.Id == "outline.grow");
        Assert.Equal(mode != "split", sample.Passes.Any(p => p.Id == "temporal.depth-store"));
        foreach (string id in new[] { "post.bloom.bright", "post.bloom.horizontal", "post.bloom.vertical", "post.bloom.composite" })
        {
            var bloom = Assert.Single(sample.Passes, p => p.Id == id);
            Assert.Equal(RendererPerfMetricStatus.NotExecuted, bloom.Duration.Status);
            Assert.Null(bloom.Duration.Value);
        }
    }

    [Theory]
    [InlineData("effects-ui")]
    [InlineData("outdoors")]
    [InlineData("legacy")]
    [InlineData("msaa-blob")]
    [InlineData("split")]
    public void Null_observer_preserves_all_feature_command_trace(string mode)
    {
        using var observed = new PostTraceRig(mode);
        using var direct = new PostTraceRig(mode);
        observed.Frame(64); direct.Frame(64);
        observed.Frame(65); direct.Frame(65);
        var observer = new PostTraceObserver(observed.Commands);
        observed.Scene.PassObserver = observer;
        observed.Frame(66); direct.Frame(66);
        Assert.Contains(observer.Passes, p => p.Key.Id == "post.blit");
        Assert.Equal(Trace(direct.Commands), Trace(observed.Commands));
        Assert.Equal(direct.Device.Lists.Count, observed.Device.Lists.Count);
        for (int i = 0; i < direct.Device.Lists.Count; i++)
            Assert.Equal(Trace(direct.Device.Lists[i]), Trace(observed.Device.Lists[i]));
        Assert.Equal(direct.Device.Native.Submits, observed.Device.Native.Submits);
        Assert.Equal(direct.Device.Waits, observed.Device.Waits);
        Assert.Equal(1, observed.Device.Native.PeakOpenLists);
        var recorder = new RendererPerfRecorder(1, RendererPerfLayout.TotalOnly, [], () => 0, 1000);
        observed.Scene.PassObserver = new RendererPerfPassObserver(recorder, RendererPerfLayout.TotalOnly, []);
        recorder.BeginFrame(0, 0, 0);
        observed.Frame(67, RendererPerfCommandList.Wrap(observed.Commands, recorder));
        recorder.EndFrame();
        Assert.Empty(recorder.Harvest(0).Passes);
        Assert.Empty(recorder.Harvest(0).Errors);
        Assert.Equal(2u, recorder.MarkerCount);
    }

    static GeometryTraceCommands.Event[] Trace(GeometryTraceCommands commands) =>
        commands.Events.Select(e => e with { Owner = null, Pipeline = null }).ToArray();

    static bool IsDraw(GeometryTraceCommands.Event e, string shader) =>
        e.Op.StartsWith("draw", StringComparison.Ordinal)
        && e.Pipeline is FakePipeline { Request: { } request } && request.FragmentGlsl == shader;

    [Fact]
    public void Prime_observer_failure_survives_render_entry()
    {
        using var rig = new PostTraceRig("outdoors");
        var failure = new InvalidOperationException("prime observer");
        rig.Scene.PassObserver = new PrimeFailure(failure);
        var error = Assert.Throws<InvalidOperationException>(() => rig.Frame(64));
        Assert.Same(failure, error.InnerException);
        Assert.Same(failure, rig.Scene.PassObserverFailure);
        Assert.Equal(1, rig.Scene.LastWaterStats.OceanStalls);
        Assert.Equal(0, rig.Device.Native.OpenLists);
        Assert.Contains(rig.Commands.Events, e => e.Op == "draw");
    }

    [Theory]
    [InlineData(SceneDebugView.MotionVectors)]
    [InlineData(SceneDebugView.Disocclusion)]
    [InlineData(SceneDebugView.Reactive)]
    public void Debug_views_own_their_commands(SceneDebugView view)
    {
        using var rig = new PostTraceRig("effects-ui");
        rig.Scene.DebugView = view;
        var observer = new PostTraceObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        rig.Frame(64);
        Assert.Contains(rig.Commands.Events, e => e.Op == "draw" && e.Owner == new RenderPassKey("display.debug"));
        Assert.All(rig.Commands.Events.Where(e => e.Op is not ("begin" or "end")), e => Assert.NotNull(e.Owner));
    }

    sealed class PrimeFailure(Exception failure) : IRenderPassObserver
    {
        public void BeginPass(IGpuCommandList cl, RenderPassKey key) { if (key.Id == "startup.ocean-prime") throw failure; }
        public void EndPass(IGpuCommandList cl, RenderPassKey key) { }
        public void SkipPass(IGpuCommandList cl, RenderPassKey key) { }
        public void BeginCpuPhase(string id) { }
        public void EndCpuPhase(string id) { }
    }

}
