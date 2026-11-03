using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Windowing;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

[Collection("AllocSensitive")]
public sealed class FrameDiagnosticsEarlySubmitTests
{
    [Fact]
    public void Schema_12_keeps_the_candidate_on_and_rotates_three_additional_gates()
    {
        string directory = Path.Combine("/tmp/khaozengine-mac-frame-performance/early-submit-ab", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "result.json");
        try
        {
            var gatesApplied = new Gates();
            using (var recorder = new FrameDiagnosticsRecorder(path, 1000, capacity: 300, shaderGates: gatesApplied))
            {
                File.WriteAllText(path + ".start", "");
                var window = new FrameDiagnosticsWindowState(true, false, false, 800, 600, 0, false);
                recorder.Observe(.001, 1, window, default);
                for (int boundary = 0; boundary <= 300; boundary++)
                    recorder.Observe(.001, 1000 + boundary, window, default);
            }
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            Assert.Equal(12, root.GetProperty("schemaVersion").GetInt32());
            var early = root.GetProperty("earlySubmit");
            Assert.Equal(72, early.GetProperty("blockCallbacks").GetInt32());
            var blocks = early.GetProperty("schedule").EnumerateArray().ToArray();
            Assert.Equal(new[] { true, true, true, true, true }, blocks.Select(b => b.GetProperty("enabled").GetBoolean()));
            Assert.Equal(new[] { 0, 72, 144, 216, 288 }, blocks.Select(b => b.GetProperty("startCallback").GetInt32()));
            Assert.Equal(new[] { 72, 144, 216, 288, 300 }, blocks.Select(b => b.GetProperty("endCallbackExclusive").GetInt32()));
            Assert.Equal(300, early.GetProperty("appliedPerCallback").GetArrayLength());
            Assert.All(early.GetProperty("appliedPerCallback").EnumerateArray(), v => Assert.False(v.GetBoolean()));
            Assert.Equal(0, early.GetProperty("appliedCallbacks").GetInt32());
            Assert.Equal(0, gatesApplied.Mask);
            var gateReport = root.GetProperty("shaderGates");
            Assert.Equal(new[] { 1, 9, 17, 33, 1 }, gateReport.GetProperty("appliedPerCallback").EnumerateArray()
                .Where((_, i) => i % 72 == 0).Select(v => v.GetInt32()));
            Assert.Equal(300, gateReport.GetProperty("exactSizePerCallback").GetArrayLength());
            Assert.All(gateReport.GetProperty("exactSizePerCallback").EnumerateArray(), v => Assert.Equal(1, v.GetInt32()));
            var gates = gateReport.GetProperty("schedule").EnumerateArray().ToArray();
            Assert.NotEmpty(gates);
            Assert.Equal(new[] { 1, 9, 17, 33, 1 }, gates.Select(b => b.GetProperty("gates").GetInt32()));
            Assert.Equal(new[] { "candidate", "lanczosExactSizeReuse", "zeroSpecular", "tileSlotReuse", "candidate" },
                gates.Select(b => b.GetProperty("state").GetString()));
        }
        finally { Directory.Delete(directory, true); }
    }

    sealed class Gates : IFrameDiagnosticsShaderGates
    {
        internal int Mask = -1;
        public void Apply(int gates) => Mask = gates;
        public int ConsumeApplied() => Mask;
        public int ConsumeExactSizeCondition() => 1;
    }

}

public sealed class FrameDiagnosticsEarlySubmitSummaryTests
{
    [Fact]
    public void State_math_joins_both_buffers_excludes_transition_and_missing_frames_and_counts_overlap_as_zero_idle()
    {
        var timing = new FrameTimingSnapshot(
            new([
                new(1, 8, 10, 70, FrameTimingCommandKind.FrameRecording),
                new(1, 8, 20, 71, FrameTimingCommandKind.FrameRecording),
                new(3, 8, 30, 72, FrameTimingCommandKind.EarlyFrameRecording),
                new(4, 8, 31, 72, FrameTimingCommandKind.FrameRecording),
                new(5, 8, 40, 73, FrameTimingCommandKind.EarlyFrameRecording),
                new(6, 8, 41, 73, FrameTimingCommandKind.FrameRecording),
                new(7, 8, 50, 74, FrameTimingCommandKind.EarlyFrameRecording),
                new(8, 8, 51, 74, FrameTimingCommandKind.FrameRecording),
                new(9, 8, 60, 75, FrameTimingCommandKind.EarlyFrameRecording),
                new(10, 8, 61, 75, FrameTimingCommandKind.FrameRecording)], 0, 0),
            new([
                new(1, 8, 15, 4, 1, 1.003), new(1, 8, 25, 4, 1.004, 1.008),
                new(3, 8, 35, 4, 1.009, 1.012), new(4, 8, 36, 4, 1.013, 1.014),
                new(5, 8, 45, 4, 1.0135, 1.0155), new(6, 8, 46, 4, 1.016, 1.018),
                new(8, 8, 55, 4, 1.020, 1.022),
                new(9, 8, 65, 4, 1.023, 1.024), new(10, 8, 66, 4, 1.025, 1.026)], 0, 0),
            new([new(1, 1, 4, 31, 72), new(1, 2, 6, 41, 73), new(2, 3, 10, 61, 75)], 0, 0),
            new([new(1, 36, 2), new(1, 46, 2.008), new(2, 66, 2.03)], 0, 0), 0);
        var experiment = new FrameEarlySubmitSnapshot(72,
            [new(0, 72, 0, 72, false), new(72, 80, 72, 80, true)], new bool[80], new int[80], new int[80], 0, 0);
        experiment.AppliedPerCallback[72] = experiment.AppliedPerCallback[73] = true;
        var passes = PassTimingSnapshot.Unavailable() with
        {
            ClockPairs = new([new(0, 0, 0, 0, 1_000_000, 100), new(80, 80, 80, 80, 1_001_000_000, 1_000_100)], 0, 0),
            Encoders = new([
                new(9, 75, 0, "compute", -1, null, null, null, null, 1000, 3000, "valid"),
                new(10, 75, 0, "blit", -1, null, null, null, null, 2000, 4000, "valid")], 0, 0),
        };
        static GpuDeviceCounters Counters(double wait) => new(0, 0, 0, 0, 0, 0, 0, 0, wait);
        var baseline = new FrameDiagnosticsBoundary(0, default, Counters(10));
        var samples = Enumerable.Range(0, 80).Select(i => new FrameDiagnosticsSample(i, i + 1, i < 72 ? .01 : .008,
            .001, default, default, Counters(11 + i))).ToArray();
        var states = FrameDiagnosticsEarlySubmitSummary.States(timing, passes, experiment, baseline, samples);
        Assert.Equal(2, states.Length);
        var off = states[0];
        var on = states[1];
        Assert.False(off.Enabled);
        Assert.True(on.Enabled);
        Assert.Equal((72, 8, 2), (off.ScheduledCallbacks, on.ScheduledCallbacks, on.AppliedCallbacks));
        Assert.Equal(100, off.FramesPerSecond, 8);
        Assert.Equal(125, on.FramesPerSecond, 8);
        Assert.Equal(5, off.FrameCompletionIntervalMs.Mean, 8);
        Assert.Equal(1, off.GpuIdlePerFrameMs.Mean, 8);
        Assert.Equal(1, on.FrameCompletionIntervalMs.Count);
        Assert.Equal(4, on.FrameCompletionIntervalMs.Mean, 8);
        Assert.Equal(0, on.GpuIdlePerFrameMs.Mean);
        Assert.Equal(1, on.SettledCallbacks);
        Assert.Equal(3, on.AttributedBusyMs.Mean, 8);
        Assert.Equal(1, on.DrawableAcquireWaitMsPerCallback.Mean);
        Assert.Equal(1, on.PresentedIntervalMs.Count);
        Assert.Equal(8, on.PresentedIntervalMs.Mean, 8);
        var (oldOn, oldOff) = FrameDiagnosticsPassSummary.IntervalsBy(timing,
            experiment.Schedule.Select(b => (b.StartCallback, b.EndCallbackExclusive, b.Enabled)));
        Assert.Equal(1, oldOn.Count);
        Assert.Equal(1, oldOff.Count);
        var summary = FrameDiagnosticsGpuSummary.Create(timing, 80);
        Assert.False(summary.FrameCompletionCoverage[74]);
        Assert.True(summary.FrameCompletionCoverage[72]);
        Assert.Equal(5, summary.FrameGpuBusyMs.Count);
        Assert.Equal(3.4, summary.FrameGpuBusyMs.Mean, 8);
    }
}
