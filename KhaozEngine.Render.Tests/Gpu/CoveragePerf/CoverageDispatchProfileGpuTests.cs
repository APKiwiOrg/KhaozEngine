using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class CoverageDispatchProfileGpuTests(ITestOutputHelper output)
{
    const int WarmupFrames = 12, MeasuredFrames = 9;

    [CoverageDispatchProfileFact]
    public void Typical_fixture_reports_each_resolution_dispatch_for_1_and_32_actors()
    {
        var results = new List<object>();
        foreach (int actors in new[] { 1, 32 }) results.Add(Measure(actors));
        string json = JsonSerializer.Serialize(new
        {
            schema = "khaozengine.coverage-dispatch-profile/1",
            diagnosticOnly = true,
            timestampCaveat = "Per-dispatch timestamps perturb execution. Metal closes the encoder at every marker. These are shape measurements, not gate evidence.",
            warmupFrames = WarmupFrames,
            measuredFrames = MeasuredFrames,
            results,
        });
        string directory = Environment.GetEnvironmentVariable("KE_COVERAGE_PROFILE_RESULTS")
            ?? Path.Combine(CoveragePerfDispatch.RepositoryRoot(AppContext.BaseDirectory), "KhaozEngine.Render.Tests", "TestResults");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "coverage-dispatch-profile.json");
        File.WriteAllText(path, json + Environment.NewLine);
        output.WriteLine("JSON file: " + path);
        // Ordinary gpu-hosted-bench uploads its console log only. Keep the complete JSON in that artifact too.
        output.WriteLine("COVERAGE_DISPATCH_PROFILE_JSON=" + json);
    }

    object Measure(int actors)
    {
        var manifest = CoveragePerfManifest.Parse(new CoveragePerfFakeRun("G1").Json);
        var block = manifest.Blocks.Single(b => b.Actors == actors && b.Condition == CoveragePerfCondition.SharedOn
            && b.TimerLayout == CoveragePerfTimerLayout.Phases) with
        { TimerLayout = CoveragePerfTimerLayout.CpuOnly, WarmupFrames = WarmupFrames, MeasuredFrames = MeasuredFrames };
        CoverageDispatchProfiler? profiler = null;
        var session = new CoveragePerfNativeSession(manifest, block,
            (m, b) =>
            {
                var host = CoveragePerfNativeHost.Open(m, b);
                return host with
                {
                    Observe = (name, frame) => profiler?.Observe(name, frame),
                    Release = () =>
                    {
                        try { profiler?.Dispose(); }
                        finally { host.Release(); }
                    }
                };
            },
            new(Stopwatch.GetTimestamp, Stopwatch.Frequency));
        try
        {
            var info = session.ReadInfo();
            Assert.True(info.NativeDevice && info.TimestampSupported && !info.SoftwareAdapter);
            var samples = new CoveragePerfFrameSample[CoveragePerfManifest.BatchFrameCount];
            RunFrames(0, WarmupFrames);
            var batch = session.Typical.Scene.CoverageGpuBatchForTests!;
            profiler = new(session.Device, batch);
            batch.DispatchObserverForTests = profiler.Dispatch;
            RunFrames(WarmupFrames, MeasuredFrames);
            Assert.Equal(MeasuredFrames, profiler.Frames.Count);
            var frames = profiler.Frames;
            double total = frames.Average(f => f.ResolutionMicroseconds);
            var stages = CoverageDispatchProfiler.Phases.Select((phase, i) => new
            {
                stage = phase.ToString(),
                localSizeX = CoverageGpuSkinRefineGlsl.IsReducer(phase) ? 1 : 64,
                groups = frames[0].Groups[i],
                meanMicroseconds = frames.Average(f => f.StageMicroseconds[i]),
                maxMicroseconds = frames.Max(f => f.StageMicroseconds[i]),
                shareOfResolutionPercent = 100 * frames.Average(f => f.StageMicroseconds[i]) / total,
            }).ToArray();
            foreach (var frame in frames)
                for (int i = 0; i < stages.Length; i++) Assert.Equal(stages[i].groups, frame.Groups[i]);
            output.WriteLine($"actors={actors} backend={info.Backend} adapter={info.Adapter} fixture={session.Typical.Fixture} profile={session.Typical.Profile}");
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Resolution mean={total:F3} us max={frames.Max(f => f.ResolutionMicroseconds):F3} us, warmup={WarmupFrames}, samples={MeasuredFrames}"));
            output.WriteLine("Stage | Mean us/frame | Max us/frame | Resolution share %");
            foreach (var row in stages)
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{row.stage} | {row.meanMicroseconds:F3} | {row.maxMicroseconds:F3} | {row.shareOfResolutionPercent:F2}"));
            double gaps = total - stages.Sum(s => s.meanMicroseconds);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Outside dispatch brackets | {gaps:F3} | | {100 * gaps / total:F2}"));
            return new
            {
                actors,
                backend = info.Backend.ToString(),
                info.Adapter,
                info.Driver,
                info.Vendor,
                info.Device,
                session.Typical.FixtureFingerprint,
                session.Typical.ProfileFingerprint,
                topology = session.Typical.ConstructionTopology,
                resolutionMeanMicroseconds = total,
                resolutionMaxMicroseconds = frames.Max(f => f.ResolutionMicroseconds),
                outsideDispatchMeanMicroseconds = gaps,
                stages,
                frames,
            };

            void RunFrames(int start, int count)
            {
                for (int offset = 0; offset < count; offset += samples.Length)
                {
                    for (int i = 0; i < samples.Length; i++) session.Frame(start + offset + i, ref samples[i]);
                    session.CompleteBatch(samples);
                    profiler?.CompleteBatch();
                    foreach (var sample in samples)
                    {
                        Assert.True(sample.DeviceComplete && sample.StageComplete && !sample.Fallback, sample.Error);
                        Assert.Null(sample.Error);
                    }
                    Assert.All(session.Typical.Actors, actor => Assert.Equal(CoveragePresentation.Dispatched,
                        session.Typical.Scene.GetCoverageOutcome(actor.Frame).Presentation));
                }
            }
        }
        finally
        {
            if (session.Typical.Scene.CoverageGpuBatchForTests is { } batch) batch.DispatchObserverForTests = null;
            try { session.Dispose(); }
            finally { profiler?.Dispose(); }
        }
    }
}
