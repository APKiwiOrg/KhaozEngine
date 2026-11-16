using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu.Performance;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>A bounded report-only capture using the shared scene, fixture and native timestamp ring.</summary>
[Collection("GpuTimingSensitive")]
public sealed class TemporalDetailCostPerfGpuTests(ITestOutputHelper output)
{
    const int Warm = 4, Count = 8, Width = 2560, Height = 1440;

    [Trait("Category", "Diagnostic")]
    [GpuFact(RequiresRealGpu = true)]
    public void Native_display_reports_the_moving_field_at_1440p() => Run(TemporalUpscale.Native, TemporalHistoryDetail.Display, false);

    [Trait("Category", "Diagnostic")]
    [GpuFact(RequiresRealGpu = true)]
    public void Native_fine_reports_the_moving_field_at_1440p() => Run(TemporalUpscale.Native, TemporalHistoryDetail.Fine, false);

    [Trait("Category", "Diagnostic")]
    [GpuFact(RequiresRealGpu = true)]
    public void Quality_display_reports_the_moving_field_at_1440p() => Run(TemporalUpscale.Quality, TemporalHistoryDetail.Display, false);

    [Trait("Category", "Diagnostic")]
    [GpuFact(RequiresRealGpu = true)]
    public void Quality_fine_reports_the_moving_field_at_1440p() => Run(TemporalUpscale.Quality, TemporalHistoryDetail.Fine, false);

    [Trait("Category", "Diagnostic")]
    [GpuFact(RequiresRealGpu = true)]
    public void Native_default_display_reports_the_moving_field_at_1440p() =>
        Run(TemporalUpscale.Native, TemporalHistoryDetail.Display, false, splitDisplay: true);

    [Trait("Category", "Diagnostic")]
    [GpuFact(RequiresRealGpu = true)]
    public void Quality_default_display_reports_the_moving_field_at_1440p() =>
        Run(TemporalUpscale.Quality, TemporalHistoryDetail.Display, false, splitDisplay: true);

    [Trait("Category", "Diagnostic")]
    [GpuFact(RequiresRealGpu = true)]
    public void Native_display_reports_the_moving_field_at_1440p_with_a_key_shadow() =>
        Run(TemporalUpscale.Native, TemporalHistoryDetail.Display, true);

    [Trait("Category", "Diagnostic")]
    [GpuFact(RequiresRealGpu = true)]
    public void Native_fine_reports_the_moving_field_at_1440p_with_a_key_shadow() =>
        Run(TemporalUpscale.Native, TemporalHistoryDetail.Fine, true);

    [Trait("Category", "Diagnostic")]
    [GpuFact(RequiresRealGpu = true)]
    public void Native_default_display_reports_the_moving_field_at_1440p_with_a_key_shadow() =>
        Run(TemporalUpscale.Native, TemporalHistoryDetail.Display, true, splitDisplay: true);

    [Trait("Category", "Diagnostic")]
    [GpuFact(RequiresRealGpu = true)]
    public void Quality_display_reports_the_moving_field_at_1440p_with_a_key_shadow() =>
        Run(TemporalUpscale.Quality, TemporalHistoryDetail.Display, true);

    [Trait("Category", "Diagnostic")]
    [GpuFact(RequiresRealGpu = true)]
    public void Quality_fine_reports_the_moving_field_at_1440p_with_a_key_shadow() =>
        Run(TemporalUpscale.Quality, TemporalHistoryDetail.Fine, true);

    [Trait("Category", "Diagnostic")]
    [GpuFact(RequiresRealGpu = true)]
    public void Quality_default_display_reports_the_moving_field_at_1440p_with_a_key_shadow() =>
        Run(TemporalUpscale.Quality, TemporalHistoryDetail.Display, true, splitDisplay: true);

    void Run(TemporalUpscale preset, TemporalHistoryDetail mode, bool shadowed, bool splitDisplay = false)
    {
        var stage = new FrontStage(Width, Height, 4.5f);
        using var fixture = new TemporalFixture(Width, Height, scene =>
        {
            stage.Setup(scene, AntiAliasing.Temporal, preset);
            scene.Post.Temporal.HistoryDetail = mode;
            if (shadowed)
            {
                scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                scene.Post.LightDirection = Vector3.Normalize(new Vector3(0.4f, -0.6f, -1f));
                scene.Post.AmbientColor = new Color(0.3f, 0.3f, 0.3f, 1f);
            }
            scene.TemporalResolveEntryForTests = splitDisplay ? null : TemporalResolveEntry.Fused;
        });
        if (TemporalResolveCostPerfGpuTests.IsSoftwareDevice(fixture.Device.Capabilities.DeviceName)) return;
        string[] passes = mode == TemporalHistoryDetail.Fine
            ? ["temporal.detail-classify", "temporal.resolve", "temporal.detail-store"]
            : splitDisplay ? ["temporal.prepare", "temporal.accumulate"] : ["temporal.resolve"];
        var draw = TemporalResolveCostPerfGpuTests.FieldScene(stage);
        fixture.Frames(Warm, draw);
        Assert.Equal(mode, fixture.Scene.TemporalHistoryDetailInEffect);
        Assert.Equal(splitDisplay ? TemporalResolveEntry.Split : TemporalResolveEntry.Fused,
            fixture.Scene.TemporalResolveRendererForTests!.LastEntry);
        Assert.Equal(!shadowed, fixture.Scene.TemporalResolveRendererForTests!.LastCurrentVisibilityFullyLit);
        string sceneLabel = shadowed ? "shadowed" : "fully-lit";
        string modeLabel = splitDisplay ? "DisplaySplit" : mode.ToString();
        var results = new GpuPerfTimestampResult?[Count];
        uint markers = (uint)(2 * passes.Length + 2);
        using var ring = new GpuPerfTimestampRing(fixture.Device, Count, markers, (id, value) => results[id] = value);
        using var submission = new GpuFrameSubmission(fixture.Device);
        var observer = new PassTimes(passes);
        fixture.Scene.PassObserver = observer;
        for (int frame = 0; frame < Count; frame++)
        {
            int sample = frame;
            fixture.ProfileFrame(draw, submission, commands =>
            {
                if (!ring.TryBegin(commands, submission, sample, markers, out var pool))
                    throw new InvalidOperationException(ring.Failure);
                observer.BeginFrame(pool);
            }, commands =>
            {
                if (observer.Next != markers - 1) throw new InvalidOperationException("The pass capture is incomplete.");
                ((IGpuCommandListTimestampEnd)commands).FinishTimestampsOnEnd(ring.ActivePool!, markers - 1);
            });
            ring.AwaitAvailability(Stopwatch.GetTimestamp, long.MaxValue);
            ring.PollCompleted();
        }
        fixture.Scene.PassObserver = null;
        if (!ring.CompleteBlock()) throw new InvalidOperationException(ring.Failure);
        byte[] visibility = TemporalVisibilityHistoryGpuTests.Read(fixture.Device, fixture.Scene.ResolvedVisibilityTarget!);
        int shadowPixels = visibility.Count(value => value < byte.MaxValue);
        if (shadowed) Assert.True(shadowPixels > 0, "The shadowed cost scene must contain a visible key shadow.");
        output.WriteLine($"COST|{DateTime.UtcNow:O}|{fixture.Device.Backend}|{fixture.Device.Capabilities.DeviceName}"
            + $"|{preset}|{modeLabel}|{Width}x{Height}|moving thin field|scene={sceneLabel}|warm={Warm}|measured={Count}|shared machine"
            + $"|internal={fixture.Scene.CurrentFrameView.Width}x{fixture.Scene.CurrentFrameView.Height}"
            + $"|certifiedCurrent={fixture.Scene.TemporalResolveRendererForTests!.LastCurrentVisibilityFullyLit}"
            + $"|visibilitySkipped={fixture.Scene.TemporalResolveRendererForTests!.LastVisibilityDecisionSkipped}"
            + $"|shadowPixels={shadowPixels}");
        for (int pass = 0; pass < passes.Length; pass++)
        {
            double[] times = results.Select(result =>
            {
                if (result is not { Valid: true, Period: { } period })
                    throw new InvalidOperationException("Native timestamps were invalid or absent.");
                return (result.Ticks[2 * pass + 2] - result.Ticks[2 * pass + 1]) * period / 1e6;
            }).ToArray();
            double[] ordered = times.Order().ToArray();
            output.WriteLine($"PASS|{preset}|{modeLabel}|{passes[pass]}|medianMs={F((ordered[3] + ordered[4]) / 2)}"
                + $"|minMs={F(ordered[0])}|maxMs={F(ordered[^1])}|samplesMs={string.Join(',', times.Select(F))}|scene={sceneLabel}");
        }
    }

    static string F(double value) => value.ToString("0.000000", CultureInfo.InvariantCulture);

    sealed class PassTimes(string[] passes) : IRenderPassObserver
    {
        IGpuTimestampPool? _pool;
        readonly HashSet<string> _selected = new(passes, StringComparer.Ordinal);
        internal uint Next { get; private set; }
        internal void BeginFrame(IGpuTimestampPool pool) { _pool = pool; Next = 1; }
        public void BeginPass(IGpuCommandList commands, RenderPassKey key)
        {
            if (_selected.Contains(key.Id)) commands.WriteTimestamp(_pool!, Next++);
        }
        public void EndPass(IGpuCommandList commands, RenderPassKey key)
        {
            if (_selected.Contains(key.Id)) commands.WriteTimestamp(_pool!, Next++);
        }
        public void SkipPass(IGpuCommandList commands, RenderPassKey key) { }
        public void BeginCpuPhase(string id) { }
        public void EndCpuPhase(string id) { }
    }
}
