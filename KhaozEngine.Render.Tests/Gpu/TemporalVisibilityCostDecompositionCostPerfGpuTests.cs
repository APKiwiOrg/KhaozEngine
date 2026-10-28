using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Head-only diagnostic, never a budget assertion. The resolve cost fixture's exact boxes and moving field at
/// 2560x1440 Native and Quality, with V0 shipped, V1 without the check, V3 frozen D prime and V6 without the check
/// and with identity lock barriers, each fused and split,
/// plus Off at the internal size. Every variant is a rewrite through the renderer's fragment source hook. The
/// retired V5 (visibility infrastructure off) and V4 (V5 without the lock release's precise markings) needed a
/// production switch, and their run 39519219228 is recorded in benchmark-result-2.md. Fifteen rounds rotate blocks of twelve frames after two
/// warm frames. Independent fixture copies keep every mode on the same scene frame and jitter phase, with its own
/// history, rather than letting the moving field advance between variants. Nothing compiles inside a timed block.
/// </summary>
[Collection("GpuTimingSensitive")]
public sealed class TemporalVisibilityCostDecompositionCostPerfGpuTests(ITestOutputHelper output)
{
    const int W = 2560, H = 1440, Rounds = 15, Warm = 2, Block = 12, Settle = 10;

    // V0 runs the scene's own renderer. The others install a rewrite before the first resolve.
    internal static readonly (string Label, Func<string, string>? Rewrite)[] Variants =
    [
        ("V0 shipped", null), ("V1 check removed", TemporalVisibilityCostPrograms.WithoutCheck),
        ("V3 D", TemporalVisibilityFrozenD.ToD),
        ("V6 no check, identity barriers", source =>
            TemporalVisibilityCostPrograms.WithoutBarriers(TemporalVisibilityCostPrograms.WithoutCheck(source))),
    ];

    [GpuFact(RequiresRealGpu = true)]
    public void TheBoxesAtNativeAreDecomposedAt1440p() => Run(field: false, TemporalUpscale.Native);

    [GpuFact(RequiresRealGpu = true)]
    public void TheBoxesAtQualityAreDecomposedAt1440p() => Run(field: false, TemporalUpscale.Quality);

    [GpuFact(RequiresRealGpu = true)]
    public void TheMovingFieldAtNativeIsDecomposedAt1440p() => Run(field: true, TemporalUpscale.Native);

    [GpuFact(RequiresRealGpu = true)]
    public void TheMovingFieldAtQualityIsDecomposedAt1440p() => Run(field: true, TemporalUpscale.Quality);

    // temporalStoredLock: a mark's level is an odd whole number below zero, so the remainder of minus one minus the
    // stored value over two is the lock.
    static float StoredLock(float stored)
    {
        if (!(stored < -0.5f)) return stored;
        float x = -1f - stored;
        return x - 2f * MathF.Floor(x / 2f);
    }

    sealed class Mode : IDisposable
    {
        internal readonly string Label;
        internal readonly TemporalFixture Fx;
        internal readonly TemporalResolveEntry? Entry;
        internal readonly List<double> Wall = [], Submit = [];
        internal int LastFrame = -1;
        readonly Action<Scene3D, int> _draw;

        internal Mode(string label, TemporalUpscale preset, bool field, TemporalResolveEntry? entry,
            Func<string, string>? rewrite, int iw = 0, int ih = 0)
        {
            Label = label;
            Entry = entry;
            var stage = new FrontStage(W, H, 4.5f);
            Fx = new TemporalFixture(W, H, s => stage.Setup(s,
                entry is null ? AntiAliasing.Off : AntiAliasing.Temporal, preset));
            Action<Scene3D, int> draw = field ? TemporalResolveCostPerfGpuTests.FieldScene(stage)
                : TemporalResolveCostPerfGpuTests.BoxesScene(stage);
            _draw = (s, n) => { LastFrame = n; draw(s, n); };
            Fx.Scene.TemporalResolveEntryForTests = entry;
            if (rewrite is not null) TemporalTestResolve.Install(Fx, rewrite);
            if (entry is null)
            {
                Fx.Scene.Post.RenderScale = RenderScale.FixedInternal;
                Fx.Scene.Post.RenderWidth = iw;
                Fx.Scene.Post.RenderHeight = ih;
            }
        }

        internal void Frames(int count) => Fx.Frames(count, _draw);
        public void Dispose() => Fx.Dispose();
    }

    void Run(bool field, TemporalUpscale preset)
    {
        // Match the original cost fixture's software early return before measuring or building extra fixtures.
        using var shipped = new Mode("V0 shipped fused", preset, field, TemporalResolveEntry.Fused, null);
        string device = shipped.Fx.Device.Capabilities.DeviceName;
        output.WriteLine($"{shipped.Fx.Device.Backend} on {device}");
        if (TemporalResolveCostPerfGpuTests.IsSoftwareDevice(device))
        {
            output.WriteLine("  not measured: a software rasteriser says nothing about a GPU's cost");
            return;
        }
        var modes = new List<Mode> { shipped };
        try
        {
            shipped.Frames(Settle - 2);
            shipped.Fx.Scene.RequestTemporalCounts();
            shipped.Frames(2);
            TemporalDiagnostics counts = shipped.Fx.Scene.LastTemporalDiagnostics;
            Assert.True(counts.CountsFrameIndex >= 0, "the resolve's counts were never sampled");
            int iw = counts.InternalWidth, ih = counts.InternalHeight;
            foreach (TemporalResolveEntry entry in new[] { TemporalResolveEntry.Fused, TemporalResolveEntry.Split })
                foreach ((string label, Func<string, string>? rewrite) in Variants)
                {
                    if (entry == TemporalResolveEntry.Fused && rewrite is null) continue;
                    var mode = new Mode(label + " " + entry.ToString().ToLowerInvariant(), preset, field, entry, rewrite);
                    modes.Add(mode);
                    mode.Frames(Settle);
                }
            var off = new Mode("Off drift control", preset, field, null, null, iw, ih);
            modes.Insert(0, off);
            off.Frames(Settle);
            ReportBarrierControl(modes, field, "settled");
            for (int round = 0; round < Rounds; round++)
                for (int k = 0; k < modes.Count; k++)
                {
                    Mode mode = modes[(round + k) % modes.Count];
                    mode.Frames(Warm);
                    double submitted = 0;
                    long t0 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < Block; i++)
                    {
                        mode.Frames(1);
                        submitted += mode.Fx.LastSubmitMilliseconds;
                    }
                    mode.Wall.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds / Block);
                    mode.Submit.Add(submitted / Block);
                    Assert.Equal(Settle + (round + 1) * (Warm + Block) - 1, mode.LastFrame);
                    Assert.Equal(mode.Entry is not null, mode.Fx.Scene.ResolvedLastRenderForTests);
                    Assert.Equal((iw, ih), (mode.Fx.Scene.CurrentFrameView.Width, mode.Fx.Scene.CurrentFrameView.Height));
                    if (mode.Entry is { } entry)
                    {
                        var renderer = mode.Fx.Scene.TemporalResolveRendererForTests!;
                        Assert.Equal(entry, renderer.LastEntry);
                        Assert.NotNull(mode.Fx.Scene.ResolvedVisibilityTarget);
                        Assert.True(renderer.VisibilityHistory.Allocated);
                        Assert.True(renderer.VisibilityHistory.StoredThisFrame);
                    }
                }
            ReportBarrierControl(modes, field, "completed");
            double offMedian = Median(off.Wall);
            output.WriteLine($"  {W}x{H} {preset}, {(field ? "moving thin field" : "twelve keyed boxes")}, internal {iw}x{ih}: "
                + $"{Rounds} rounds of {Block} frames after {Warm}, one block a mode a round, the order rotating");
            output.WriteLine($"  Off drift control at {iw}x{ih}: {F(offMedian)} ms a frame, blocks {Join(off.Wall)}");
            foreach (Mode mode in modes.Skip(1))
            {
                List<double> paired = mode.Wall.Zip(off.Wall, (a, b) => a - b).ToList();
                output.WriteLine($"  {mode.Label}: {F(Median(mode.Wall))} ms a frame, resolve and sharpen at most "
                    + $"{F(Median(mode.Wall) - offMedian)} ms, paired round differences median {F(Median(paired))} "
                    + $"min {F(paired.Min())} max {F(paired.Max())}, submit and drain difference "
                    + F(Median(mode.Submit) - Median(off.Submit)));
                output.WriteLine($"    blocks {Join(mode.Wall)}");
            }
            foreach (TemporalResolveEntry entry in new[] { TemporalResolveEntry.Fused, TemporalResolveEntry.Split })
            {
                Mode[] v = modes.Where(m => m.Entry == entry).ToArray();
                PrintPair(entry + " V0 minus V1 (check)", v[0], v[1]);
                PrintPair(entry + " V0 minus V3 (7f against D)", v[0], v[2]);
                PrintPair(entry + " V1 minus V6 (lock barriers)", v[1], v[3]);
            }
            output.WriteLine($"  policy {TemporalResolvePolicy.Measured}, forced {TemporalResolvePolicy.Forced}, "
                + $"counts frame {counts.CountsFrameIndex}: {counts.DisoccludedPixels} disoccluded, "
                + $"{counts.ClippedPixels} clipped of {W * H} display pixels. No budget asserted.");
        }
        finally
        {
            foreach (Mode mode in modes) if (!ReferenceEquals(mode, shipped)) mode.Dispose();
        }
    }

    void PrintPair(string what, Mode a, Mode b)
    {
        List<double> paired = a.Wall.Zip(b.Wall, (x, y) => x - y).ToList();
        output.WriteLine($"  {what}: {F(Median(a.Wall) - Median(b.Wall))} ms from the medians, paired round differences "
            + $"median {F(Median(paired))} min {F(paired.Min())} max {F(paired.Max())}");
    }

    // Read before and after all timed blocks, never inside one. V6 can change nonzero-lock rounding, so report
    // colour, confidence and locks rather than silently timing a different workload. Boxes must stay lockless.
    void ReportBarrierControl(List<Mode> modes, bool field, string phase)
    {
        foreach (TemporalResolveEntry entry in new[] { TemporalResolveEntry.Fused, TemporalResolveEntry.Split })
        {
            Mode[] v = modes.Where(m => m.Entry == entry).ToArray();
            TemporalHistorySample a = TemporalHistorySample.Read(v[1].Fx), b = TemporalHistorySample.Read(v[3].Fx);
            TemporalHistoryDifference difference = TemporalHistoryDifference.Measure(a.Colour, a.State, b.Colour, b.State);
            output.WriteLine($"  {phase} {entry} V1/V6 readback: colour [{string.Join(", ", difference.Colour.Select(c => c.ToString()))}], "
                + $"confidence {difference.Confidence}, locks {difference.Locks}");
            foreach ((string label, float[] state) in new[] { ("V1", a.State), ("V6", b.State) })
            {
                // The stored lock also marks moved, band and followed pixels as -1, -3 or -5 minus the lock, so the
                // boxes' own drifting pixels store -1 with a zero lock. Decode it as temporalStoredLock does.
                int nonzero = 0;
                for (int i = 1; i < state.Length; i += 4)
                    if (BitConverter.SingleToUInt32Bits(StoredLock(state[i])) != 0u) nonzero++;
                output.WriteLine($"    {label} {nonzero} locks without positive-zero bits of {state.Length / 4}");
                if (!field) Assert.Equal(0, nonzero);
            }
        }
    }

    static double Median(IEnumerable<double> xs)
    {
        double[] sorted = xs.OrderBy(x => x).ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
            : 0.5 * (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]);
    }

    static string F(double x) => x.ToString("0.000", CultureInfo.InvariantCulture);
    static string Join(IEnumerable<double> xs) => string.Join(" ", xs.Select(F));
}
