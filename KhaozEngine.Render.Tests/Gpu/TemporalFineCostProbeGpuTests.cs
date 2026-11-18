using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu.Performance;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Same-frame paired measurements on native Metal. Only the shipped scene advances history.</summary>
[Collection("GpuTimingSensitive")]
[Trait("Category", "Diagnostic")]
public sealed class TemporalFineCostProbeGpuTests(ITestOutputHelper output)
{
    const int Width = 2560, Height = 1440, Warm = 4, ReplayWarm = 1, Samples = 8;
    const uint MarkersPerProgram = 6, MarkersPerPair = 2 * MarkersPerProgram;
    static readonly string[] Passes = ["temporal.detail-classify", "temporal.resolve", "temporal.detail-store", "sum"];

    [GpuFact(RequiresRealGpu = true)]
    public void Native_fully_lit() => Run(TemporalUpscale.Native, false);

    [GpuFact(RequiresRealGpu = true)]
    public void Native_shadowed() => Run(TemporalUpscale.Native, true);

    [GpuFact(RequiresRealGpu = true)]
    public void Quality_fully_lit() => Run(TemporalUpscale.Quality, false);

    [GpuFact(RequiresRealGpu = true)]
    public void Quality_shadowed() => Run(TemporalUpscale.Quality, true);

    void Run(TemporalUpscale preset, bool shadowed)
    {
        var stage = new FrontStage(Width, Height, 4.5f);
        using var fixture = new TemporalFixture(Width, Height, scene =>
        {
            stage.Setup(scene, AntiAliasing.Temporal, preset);
            scene.Post.Temporal.HistoryDetail = TemporalHistoryDetail.Fine;
            if (shadowed)
            {
                scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                scene.Post.LightDirection = Vector3.Normalize(new Vector3(0.4f, -0.6f, -1f));
                scene.Post.AmbientColor = new Color(0.3f, 0.3f, 0.3f, 1f);
            }
            scene.TemporalResolveEntryForTests = TemporalResolveEntry.Fused;
        });
        Assert.Equal(GpuBackendKind.MetalNative, fixture.Device.Backend);
        Assert.False(TemporalResolveCostPerfGpuTests.IsSoftwareDevice(fixture.Device.Capabilities.DeviceName));
        var draw = TemporalResolveCostPerfGpuTests.FieldScene(stage);
        fixture.Frames(Warm, draw);
        Assert.Equal(TemporalHistoryDetail.Fine, fixture.Scene.TemporalHistoryDetailInEffect);
        Assert.Equal(TemporalResolveEntry.Fused, fixture.Scene.TemporalResolveRendererForTests!.LastEntry);

        using var replay = new TemporalFineCostProbeReplay(fixture);
        Assert.Equal(!shadowed, replay.SkipVisibility);
        byte[] visibility = TemporalVisibilityHistoryGpuTests.Read(fixture.Device, fixture.Scene.ResolvedVisibilityTarget!);
        Assert.Equal(shadowed, visibility.Any(b => b < byte.MaxValue));
        using var control = replay.Compile(TemporalFineCostVariant.Control);
        var pairs = new List<ProbePair>();
        var rejected = new List<Exception>();
        string sceneLabel = shadowed ? "shadowed" : "fully-lit";
        try
        {
            foreach (var variant in Selected())
            {
                TemporalFineCostProbeReplay.Program? program = null;
                try
                {
                    program = replay.Compile(variant);
                    var reference = variant is TemporalFineCostVariant.TwoTargetsProxy or TemporalFineCostVariant.CoreRemainder
                        ? replay.Compile(TemporalFineCostVariant.SixTargetsProxy) : null;
                    pairs.Add(new ProbePair(reference ?? control, program, reference is not null));
                }
                catch (Exception error) when (error is ShaderValidationException or InvalidOperationException)
                {
                    program?.Dispose();
                    output.WriteLine($"COST|UNMEASURED|{preset}|{variant}|scene={sceneLabel}|type={error.GetType().Name}");
                    rejected.Add(new InvalidOperationException($"Could not compile replay variant {variant}.", error));
                }
            }
            if (pairs.Count == 0) throw new AggregateException("No probe variants could run.", rejected);
            var results = Record(fixture, replay, draw, pairs, certified: !shadowed);
            output.WriteLine($"COST|{DateTime.UtcNow:O}|{fixture.Device.Backend}|{fixture.Device.Capabilities.DeviceName}"
                + $"|{preset}|FinePairedReplay|{Width}x{Height}|moving thin field|scene={sceneLabel}|warm={Warm}+{ReplayWarm}|measured={Samples}"
                + $"|internal={fixture.Scene.CurrentFrameView.Width}x{fixture.Scene.CurrentFrameView.Height}"
                + $"|certifiedSkip={replay.SkipVisibility}|shadowPixels={visibility.Count(b => b < byte.MaxValue)}");
            for (int i = 0; i < pairs.Count; i++)
            {
                ReportPair(preset, sceneLabel, pairs[i], results, i);
                if (CompareBytes(pairs[i].Variant.Variant))
                    ReportHistory(fixture, replay, draw, preset, sceneLabel, pairs[i].Variant);
            }
            if (rejected.Count > 0) throw new AggregateException("Some probe variants could not run.", rejected);
        }
        finally
        {
            foreach (var pair in pairs)
            {
                pair.Variant.Dispose();
                if (pair.OwnsReference) pair.Reference.Dispose();
            }
        }
    }

    static TemporalFineCostVariant[] Selected()
    {
        string? selected = Environment.GetEnvironmentVariable("KE_TAA_FINE_PROBE_VARIANTS");
        return string.IsNullOrWhiteSpace(selected) ? Enum.GetValues<TemporalFineCostVariant>()
            : selected.Split(',', StringSplitOptions.TrimEntries).Select(Enum.Parse<TemporalFineCostVariant>).ToArray();
    }

    static bool CompareBytes(TemporalFineCostVariant variant) => variant is TemporalFineCostVariant.Control or
        TemporalFineCostVariant.ReuseCandidateLock or TemporalFineCostVariant.UniformVisibility or TemporalFineCostVariant.EarlyGate or
        TemporalFineCostVariant.GatherGate or TemporalFineCostVariant.InlineHalo or TemporalFineCostVariant.InlineCentre or
        TemporalFineCostVariant.NoStore or TemporalFineCostVariant.ReuseAndNoStore or TemporalFineCostVariant.DirectPlanes or
        TemporalFineCostVariant.Combined;

    static GpuPerfTimestampResult[] Record(TemporalFixture fixture, TemporalFineCostProbeReplay replay,
        Action<Scene3D, int> draw, List<ProbePair> pairs, bool certified)
    {
        uint markers = 2 + (uint)pairs.Count * MarkersPerPair;
        var results = new GpuPerfTimestampResult[Samples];
        using var ring = new GpuPerfTimestampRing(fixture.Device, Samples, markers, (id, result) => results[id] = result);
        using var submission = new GpuFrameSubmission(fixture.Device);
        for (int frame = -ReplayWarm; frame < Samples; frame++)
        {
            int sample = frame;
            IGpuTimestampPool? pool = null;
            fixture.ProfileFrame(draw, submission, cl =>
            {
                Assert.Equal(certified, replay.SkipVisibility);
                replay.PrepareOptions(cl, certified);
                if (sample >= 0 && !ring.TryBegin(cl, submission, sample, markers, out pool))
                    throw new InvalidOperationException(ring.Failure);
            }, cl =>
            {
                Assert.Equal(certified, replay.SkipVisibility);
                int start = Math.Max(sample, 0) % pairs.Count;
                for (int step = 0; step < pairs.Count; step++)
                {
                    int i = (start + step) % pairs.Count;
                    var pair = pairs[i];
                    // Timestamp slots are written in execution order.
                    uint first = 1 + (uint)step * MarkersPerPair;
                    if (sample % 2 == 0)
                    {
                        replay.Record(cl, pair.Variant, pool, first);
                        replay.Record(cl, pair.Reference, pool, first + MarkersPerProgram);
                    }
                    else
                    {
                        replay.Record(cl, pair.Reference, pool, first);
                        replay.Record(cl, pair.Variant, pool, first + MarkersPerProgram);
                    }
                }
                if (pool is not null)
                    ((IGpuCommandListTimestampEnd)cl).FinishTimestampsOnEnd(pool, markers - 1);
            });
            if (sample >= 0)
            {
                ring.AwaitAvailability(Stopwatch.GetTimestamp, long.MaxValue);
                ring.PollCompleted();
            }
        }
        if (!ring.CompleteBlock()) throw new InvalidOperationException(ring.Failure);
        return results;
    }

    void ReportPair(TemporalUpscale preset, string scene, ProbePair pair, GpuPerfTimestampResult[] results, int index)
    {
        int pairs = (results[0].Ticks.Length - 2) / (int)MarkersPerPair;
        var reference = new double[Passes.Length][];
        var variant = new double[Passes.Length][];
        for (int pass = 0; pass < Passes.Length; pass++)
        {
            reference[pass] = new double[Samples];
            variant[pass] = new double[Samples];
        }
        for (int sample = 0; sample < Samples; sample++)
        {
            int executionIndex = (index - sample % pairs + pairs) % pairs;
            int first = 1 + executionIndex * (int)MarkersPerPair;
            bool variantFirst = sample % 2 == 0;
            Accumulate(pair.Reference.Variant, results[sample], reference, sample,
                variantFirst ? first + (int)MarkersPerProgram : first);
            Accumulate(pair.Variant.Variant, results[sample], variant, sample,
                variantFirst ? first : first + (int)MarkersPerProgram);
        }
        for (int pass = 0; pass < Passes.Length; pass++)
        {
            string prefix = $"PASS|{preset}|{pair.Variant.Variant}|{Passes[pass]}|scene={scene}";
            double[] saving = reference[pass].Zip(variant[pass], (a, b) => a - b).ToArray();
            ReportSamples(prefix, "reference", pair.Reference.Variant, reference[pass]);
            ReportSamples(prefix, "variant", pair.Variant.Variant, variant[pass]);
            ReportSamples(prefix, "saving", pair.Variant.Variant, saving);
        }
    }

    static void Accumulate(TemporalFineCostVariant variant, GpuPerfTimestampResult? result, double[][] times,
        int sample, int first)
    {
        if (result is not { Valid: true, Period: { } period })
            throw new InvalidOperationException("Native timestamps were invalid or absent.");
        for (int pass = 0; pass < 3; pass++)
        {
            if ((pass == 0 && !TemporalFineCostProbePrograms.Classifies(variant)) ||
                (pass == 2 && !TemporalFineCostProbePrograms.Stores(variant))) continue;
            int marker = first + 2 * pass;
            double milliseconds = (result.Ticks[marker + 1] - result.Ticks[marker]) * period / 1e6;
            times[pass][sample] = milliseconds;
            times[^1][sample] += milliseconds;
        }
    }

    void ReportSamples(string prefix, string role, TemporalFineCostVariant program, double[] samples)
    {
        double[] ordered = samples.Order().ToArray();
        output.WriteLine($"{prefix}|role={role}|program={program}|medianMs={F((ordered[3] + ordered[4]) / 2)}"
            + $"|minMs={F(ordered[0])}|maxMs={F(ordered[^1])}|samplesMs={string.Join(',', samples.Select(F))}");
    }

    void ReportHistory(TemporalFixture fixture, TemporalFineCostProbeReplay replay, Action<Scene3D, int> draw,
        TemporalUpscale preset, string scene, TemporalFineCostProbeReplay.Program program)
    {
        using var submission = new GpuFrameSubmission(fixture.Device);
        fixture.ProfileFrame(draw, submission, cl => replay.PrepareOptions(cl, replay.SkipVisibility),
            cl => replay.Record(cl, program, null, 0));
        output.WriteLine($"COST|IDENTITY|{preset}|{program.Variant}|scene={scene}|{replay.CompareHistory(program.Variant)}");
    }

    static string F(double value) => value.ToString("0.000000", CultureInfo.InvariantCulture);

    sealed record ProbePair(TemporalFineCostProbeReplay.Program Reference, TemporalFineCostProbeReplay.Program Variant,
        bool OwnsReference);
}
