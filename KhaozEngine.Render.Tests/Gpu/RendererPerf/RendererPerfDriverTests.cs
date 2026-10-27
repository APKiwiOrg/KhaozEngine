using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfDriverTests
{
    [Fact]
    public void Executes_only_manifest_order_and_exact_counts()
    {
        var rig = new DriverRig();
        RendererPerfReport report = rig.Run();
        Assert.True(report.Publication.FinalUnitPublished);
        Assert.False(report.Assessment.ValidForPromotion);
        Assert.Equal(RendererPerfCollectionState.Incomplete, report.Compiler.State);
        Assert.Empty(report.Pipelines);
        Assert.Equal(1260, rig.Frames.Count);
        Assert.Equal(new[] { "conditioning:S3", "S3", "S3", "control:1", "control:4", "control:5", "control:7", "control:9", "control:11" },
            rig.Created.Select(b => b.SceneId));
        Assert.Equal(rig.Template.Manifest.Blocks.ToArray(), rig.Created.Skip(1).Take(2).ToArray());
        Assert.Equal(420, rig.BatchSizes.Count);
        Assert.All(rig.BatchSizes, n => Assert.Equal(3, n));
        Assert.Equal(300, report.Conditioning.Length);
        Assert.Equal(2, report.Blocks.Length);
        Assert.All(report.Blocks, b =>
        {
            Assert.Equal(64, b.Warmup.Length);
            Assert.Equal(128, b.Measured.Length);
            Assert.Equal(Enumerable.Range(192, 64), b.Warmup.Select(f => f.SequenceIndex));
            Assert.Equal(Enumerable.Range(0, 128), b.Measured.Select(f => f.SequenceIndex));
            Assert.Equal(192, b.Warmup.Concat(b.Measured).Select(f => f.FrameId).Distinct().Count());
        });
        Assert.Equal(1260, report.Batches.Sum(b => b.Count));
        Assert.All(report.Batches, b => Assert.Equal(900, b.Telemetry["clock.observedMHz"].Value));
        Assert.Equal(9, rig.Disposals);
        _ = RendererPerfReportJson.Serialize(report);
    }

    [Fact]
    public void Keeps_outliers_gc_and_first_block()
    {
        var rig = new DriverRig { Outlier = true };
        RendererPerfReport report = rig.Run();
        Assert.Equal(rig.Template.Manifest.Blocks[0], report.Blocks[0].Block);
        Assert.Equal(128, report.Blocks[0].Measured.Length);
        RendererPerfFrameSample slow = Assert.Single(report.Blocks[0].Measured, f => f.SequenceIndex == 17);
        Assert.Equal(10000, slow.GpuFrame.Value);
        Assert.Equal(1, slow.Metrics["gc.gen2.collections"].Value);
        Assert.Equal(1260, rig.Frames.Count);
        Assert.Equal(2, report.Blocks.Length);
    }

    [Fact]
    public void Clock_drift_refuses_promotion_without_retry()
    {
        var rig = new DriverRig { Drift = true };
        RendererPerfReport report = rig.Run();
        Assert.False(report.Assessment.ValidForPromotion);
        Assert.Contains(report.Assessment.Reasons, r => r.Contains("ClockOrWorkDrift", StringComparison.Ordinal));
        Assert.Equal(1260, rig.Frames.Count);
        Assert.Equal(9, rig.Created.Count);
        Assert.Equal(100, report.Blocks[0].Measured[0].GpuFrame.Value);
        Assert.Equal(120, report.Blocks[0].Measured[^1].GpuFrame.Value);
        Assert.True(report.Publication.FinalUnitPublished);
    }

    [Theory]
    [InlineData("frame-timeout")]
    [InlineData("unit-timeout")]
    [InlineData("final-write")]
    [InlineData("late-final")]
    [InlineData("partial-write")]
    [InlineData("frame-failure")]
    [InlineData("completion-stamp")]
    [InlineData("late-stamp")]
    public void Timeout_or_failed_publication_preserves_partial_report(string fault)
    {
        var rig = new DriverRig { Fault = fault };
        RendererPerfReport report = rig.Run();
        Assert.False(report.Publication.FinalUnitPublished);
        Assert.False(report.Assessment.ValidForPromotion);
        Assert.True(report.Publication.ReturnedByRun);
        Assert.NotNull(report.Publication.OriginalFailure);
        Assert.NotEmpty(rig.Writes);
        Assert.Contains(rig.Writes, w => !w.Final);
        Assert.Equal(report.Publication.OriginalFailure, rig.Writes.Last(w => !w.Final).Report.Publication.OriginalFailure);
        Assert.NotEmpty(report.Conditioning);
        Assert.True(rig.Frames.Count <= 1260);
        Assert.Equal(rig.Created.Count, rig.Disposals);
        _ = RendererPerfReportJson.Serialize(report);
        if (fault == "frame-failure") Assert.Contains("frame failed", report.Publication.OriginalFailure);
        if (fault == "frame-timeout") Assert.Single(rig.Created);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("no-timestamp")]
    [InlineData("adapter")]
    [InlineData("runtime")]
    public void Admission_refuses_before_clock_or_frame(string fault)
    {
        var rig = new DriverRig();
        RendererPerfRunIdentity identity = rig.Template.Identity;
        if (fault == "no-timestamp") identity = identity with { TimestampSupported = false };
        if (fault == "adapter") identity = identity with { DeviceId = 1 };
        if (fault == "runtime") identity = identity with { Runtime = "10.0.1" };
        rig.Enabled = fault != "disabled";
        Assert.Throws<RendererPerfProtocolException>(() => rig.Run(identity));
        Assert.Equal(0, rig.ClockCalls);
        Assert.Empty(rig.Frames);
    }

    [Fact]
    public void Full_schedule_keeps_final_short_batches_and_all_iterations()
    {
        var rig = new DriverRig(full: true);
        RendererPerfReport report = rig.Run();
        Assert.Equal(16944, rig.Frames.Count);
        Assert.Equal(5668, rig.BatchSizes.Count);
        Assert.Equal(30, rig.BatchSizes.Count(n => n == 1));
        Assert.Equal(30, report.Blocks.Length);
        Assert.All(report.Blocks, b =>
        {
            Assert.Equal(192, b.Warmup.Length);
            Assert.Equal(256, b.Measured.Length);
        });
        Assert.True(report.Publication.FinalUnitPublished);
        Assert.False(report.Assessment.ValidForPromotion);
        Assert.Equal(RendererPerfCollectionState.Incomplete, report.Compiler.State);
        _ = RendererPerfReportJson.Serialize(report);
    }

    [Fact]
    public void Frame_failure_remains_original_when_drain_and_cleanup_fail()
    {
        var rig = new DriverRig { Fault = "frame-and-drain-failure" };
        RendererPerfReport report = rig.Run();
        Assert.Equal("frame failed", report.Publication.OriginalFailure);
        Assert.Contains("drain failed", report.Publication.Errors);
        Assert.Contains("cleanup failed", report.Publication.Errors);
        Assert.Single(report.Conditioning);
    }

    [Fact]
    public void Known_frame_failure_stops_before_the_next_measurement()
    {
        var rig = new DriverRig { Fault = "sample-failure" };
        var report = rig.Run();
        Assert.Single(rig.Frames);
        Assert.Single(report.Conditioning);
        Assert.Equal("known frame failed", report.Publication.OriginalFailure);
    }

    [Fact]
    public void Unit_deadline_includes_admitted_cold_setup()
    {
        var rig = new DriverRig { Fault = "startup-timeout" };
        var report = rig.Run();
        Assert.Empty(rig.Frames);
        Assert.False(report.Publication.FinalUnitPublished);
        Assert.Contains("UnitTimeout", report.Publication.OriginalFailure);
        Assert.Single(report.Startup);
    }

    [Fact]
    public void Failed_batch_observation_stops_the_current_block()
    {
        var rig = new DriverRig { Fault = "observation-failure" };
        var report = rig.Run();
        Assert.Equal(3, rig.Frames.Count);
        Assert.Single(rig.Created);
        Assert.Equal("observation failed", report.Publication.OriginalFailure);
    }

    internal sealed class DriverRig
    {
        internal readonly RendererPerfReport Template;
        internal DriverRig(bool full = false, RendererPerfReport? template = null)
        {
            Template = template ?? RendererPerfProtocolTests.CompleteReport();
            if (full) Template = Template with
            {
                Manifest = RendererPerfManifest.Parse(Encoding.UTF8.GetBytes(
                RendererPerfProtocolTests.ManifestJson("full").ToJsonString()))
            };
        }
        internal readonly List<RendererPerfBlock> Created = [];
        internal readonly List<(RendererPerfBlock Block, int Index)> Frames = [];
        internal readonly List<int> BatchSizes = [];
        internal readonly List<(RendererPerfReport Report, bool Final)> Writes = [];
        internal bool Outlier, Drift, Enabled = true;
        internal string? Fault;
        internal int Disposals, ClockCalls;
        internal long Tick;
        internal long FirstBatchFrameTicks = 1, FirstDrainTicks = 1, ColdReadbackTicks;
        internal Func<RendererPerfBlock, int, long>? FrameTicks;
        internal Func<RendererPerfBlock, RendererPerfFrameSample, RendererPerfFrameSample>? RetainFrame;
        internal bool ObserveMemory;
        internal RendererPerfClock Clock => new(() => { ClockCalls++; return Tick; }, 1000);
        internal RendererPerfReport Run(RendererPerfRunIdentity? identity = null)
        {
            var publication = new RendererPerfPublicationWriter((report, final) =>
            {
                Writes.Add((report, final));
                if (final && Fault == "slow-final" && Writes.Count(w => w.Final) == 1) Tick += 5000;
                if (final && Fault == "late-final") Tick += 601000;
                if (final && Fault == "final-write") throw new InvalidOperationException("final write failed");
                if (!final && Fault == "partial-write" && Writes.Count == 1) throw new InvalidOperationException("partial write failed");
            }, completed =>
            {
                if (Fault == "completion-stamp") throw new InvalidOperationException("completion stamp failed");
                if (Fault == "late-stamp") Tick += 601000;
                Writes[^1] = (completed, true);
            });
            return RendererPerfDriver.Run(Template.Manifest, identity ?? Template.Identity, (m, b, c) =>
            {
                Created.Add(b);
                return new Session(this, b, identity ?? Template.Identity);
            }, Clock, publication);
        }

        sealed class Session(DriverRig rig, RendererPerfBlock block, RendererPerfRunIdentity identity) : IRendererPerfSession
        {
            int _next, _pending;
            RendererPerfCpuSpan? _drain, _harvest;
            public void Frame(int frameIndex, ref RendererPerfFrameSample sample)
            {
                rig.Frames.Add((block, frameIndex));
                _pending++;
                if (_pending > 3) throw new InvalidOperationException("Undrained batch.");
                long start = rig.Tick;
                rig.Tick += rig.FrameTicks?.Invoke(block, _next) ?? (rig.Fault == "frame-timeout" ? 10001
                    : rig.Created.Count == 1 && _next < 3 ? rig.FirstBatchFrameTicks : 1);
                ulong duration = rig.Drift && frameIndex >= 64 ? 120UL : 100UL;
                if (rig.Fault == "gpu-timeout") duration = 10000000001;
                bool slow = rig.Outlier && block.SceneId == "S3" && frameIndex == 17;
                if (slow) duration = 10000;
                sample = new(_next++, RendererPerfRecipe.SequenceIndex(frameIndex), start, rig.Tick, 1000,
                    duration, 1, Value(duration, "ns"), block.Layout == RendererPerfLayout.PerPass
                        ? [new("device.scene.begin", 0, null, null, 0, 0, null,
                            new(null, "ns", "CPU-only device owner", RendererPerfMetricStatus.Unsupported, "no GPU interval"), true)] : [], [],
                    ImmutableDictionary<string, RendererPerfMetric>.Empty.Add("gc.gen2.collections", Value(slow ? 1 : 0, "count")), rig.Fault == "sample-failure" ? ["known frame failed"] : []);
                if (rig.Fault is "frame-failure" or "frame-and-drain-failure") throw new InvalidOperationException("frame failed");
            }
            public void CompleteBatch(Span<RendererPerfFrameSample> samples)
            {
                Assert.Equal(_pending, samples.Length);
                rig.BatchSizes.Add(samples.Length);
                _pending = 0;
                _drain = _harvest = null;
                long start = rig.Tick;
                rig.Tick += rig.Fault == "drain-timeout" ? 10001 : rig.Fault == "unit-timeout" ? 601000
                    : rig.Created.Count == 1 && _next == 3 ? rig.FirstDrainTicks : 1;
                if (rig.Fault == "frame-and-drain-failure") throw new InvalidOperationException("drain failed");
                if (rig.Fault == "second-drain" && block.Layout == RendererPerfLayout.PerPass && _next == 6)
                    throw new InvalidOperationException("second drain failed");
                _drain = new("batch.completionDrain", null, start, rig.Tick);
                _harvest = new("batch.harvest", null, rig.Tick, rig.Tick);
                if (rig.RetainFrame is { } retain)
                    for (int i = 0; i < samples.Length; i++) samples[i] = retain(block, samples[i]);
                if (rig.Created.Count == 1 && _next == block.MeasuredFrames) rig.Tick += rig.ColdReadbackTicks;
            }
            public RendererPerfSessionInfo ReadInfo()
            {
                if (rig.Fault == "observation-failure" && _next > 0) throw new InvalidOperationException("observation failed");
                if (rig.Fault == "second-observation" && block.Layout == RendererPerfLayout.PerPass && _next == 6)
                    throw new InvalidOperationException("second observation failed");
                return new(identity, rig.Enabled, true,
                    rig.Template.Manifest.Recipes.FirstOrDefault(r => r.SceneId == (block.SceneId == "conditioning:S3" ? "S3" : block.SceneId)) is { } recipe
                        ? new(recipe, "public primitives", new string('b', 64), ImmutableDictionary<string, long>.Empty,
                            ImmutableDictionary<string, long>.Empty, ImmutableDictionary<string, RendererPerfMetric>.Empty) : null,
                    ImmutableDictionary<string, RendererPerfMetric>.Empty.Add("clock.observedMHz", Value(900, "MHz")),
                    ControlStateSha256: block.SceneId.StartsWith("control:", StringComparison.Ordinal) ? new string('b', 64) : null,
                    Memory: rig.ObserveMemory ? new([], ImmutableDictionary<string, RendererPerfMetric>.Empty
                        .Add("process.workingSet.bytes", Value(rig.Created.Count == 1 && _next == 3 ? 9000 : 100, "bytes"))
                        .Add("managed.live.bytes", Value(rig.Created.Count == 1 && _next == 3 ? 6000 : 50, "bytes"))) : null,
                    Drain: _drain, Harvest: _harvest,
                    Startup: rig.Fault == "startup-timeout" ? new(block.SceneId, new("startup.cold", null, -601000, 0), 1000,
                        ImmutableDictionary<string, RendererPerfMetric>.Empty) : null);
            }
            public void Dispose()
            {
                rig.Disposals++;
                if (rig.Fault == "frame-and-drain-failure") throw new InvalidOperationException("cleanup failed");
            }
        }
    }
    internal static RendererPerfMetric Value(double value, string unit) => new(value, unit, "held fake", RendererPerfMetricStatus.Available, null);
}
