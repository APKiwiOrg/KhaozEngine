using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

[Collection("AllocSensitive")]
public sealed class FrameDiagnosticsShaderGateTests : IDisposable
{
    readonly string _directory = Path.Combine("/tmp/khaozengine-mac-frame-performance/shader-gates-ab", "gate-tests-" + Guid.NewGuid().ToString("N"));
    string ResultPath => Path.Combine(_directory, "result.json");
    static FrameDiagnosticsWindowState Foreground => new(true, false, false, 800, 600, 0, false);

    public FrameDiagnosticsShaderGateTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    sealed class FakeGates : IFrameDiagnosticsShaderGates
    {
        internal int Gates = -99;
        internal int Consume = int.MinValue;
        public void Apply(int gates) => Gates = gates;
        public int ConsumeApplied()
        {
            int value = Consume == int.MinValue ? Gates : Consume;
            Consume = int.MinValue;
            return value;
        }
    }

    FrameDiagnosticsRecorder Recorder(FakeGates gates, int capacity) => new(ResultPath, 1000, capacity, shaderGates: gates, shaderGateBlocks: true);
    void Trigger() => File.WriteAllText(ResultPath + ".start", "");
    static void Observe(FrameDiagnosticsRecorder recorder, long timestamp) =>
        recorder.Observe(0.001, timestamp, Foreground, default);

    [Theory]
    [InlineData(300)]   // Stops in a baseline block
    [InlineData(100)]   // Stops inside the first gate's block, which has to turn it off
    public void Gates_stay_untouched_until_sampling_rotate_from_baseline_and_end_off(int capacity)
    {
        var gates = new FakeGates();
        using var recorder = Recorder(gates, capacity);
        Observe(recorder, 1);
        Assert.Equal(-99, gates.Gates);
        Trigger();
        Observe(recorder, 1000);   // Consumes the trigger. Armed, not sampling.
        Assert.Equal(-99, gates.Gates);
        int[] rotation = [0, 1, 2, 4];
        for (int callback = 0; callback < capacity; callback++)
        {
            Observe(recorder, 2000 + callback);
            Assert.Equal(rotation[callback / 72 % 4], gates.Gates);
        }
        Observe(recorder, 2000 + capacity);
        Assert.Equal(0, gates.Gates);
        Observe(recorder, 3000 + capacity);
        Assert.Equal(0, gates.Gates);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_capture_that_fails_or_closes_inside_a_gate_block_turns_it_off(bool close)
    {
        var gates = new FakeGates();
        var recorder = Recorder(gates, 200);
        Trigger();
        Observe(recorder, 1000);
        for (int callback = 0; callback <= 72; callback++) Observe(recorder, 2000 + callback);
        Assert.Equal(1, gates.Gates);
        if (close) recorder.Dispose();
        else Observe(recorder, 2000);   // A non-increasing boundary fails the capture.
        Assert.Equal(0, gates.Gates);
        Assert.NotNull(recorder.Failure);
        recorder.Dispose();
    }

    [Fact]
    public void Schema_10_reports_the_schedule_the_state_each_callback_applied_and_counts_that_expose_a_mismatch()
    {
        var gates = new FakeGates();
        var recorder = Recorder(gates, 150);
        Trigger();
        Observe(recorder, 1000);
        Observe(recorder, 2000);       // Baseline for callback 0.
        for (int callback = 1; callback <= 150; callback++)
        {
            // The value read at this boundary describes the callback that just ended.
            if (callback - 1 == 3) gates.Consume = -1;
            if (callback - 1 == 80) gates.Consume = 4;
            Observe(recorder, 2000 + callback);
        }
        recorder.Dispose();
        Assert.Null(recorder.Failure);

        using var json = JsonDocument.Parse(File.ReadAllText(ResultPath));
        JsonElement report = json.RootElement.GetProperty("shaderGates");
        Assert.Equal(11, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(new[] { "pointShadowZeroNdl", "underwaterMarchZero", "rigidWorldReuse" },
            report.GetProperty("names").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(72, report.GetProperty("blockCallbacks").GetInt32());
        JsonElement[] schedule = report.GetProperty("schedule").EnumerateArray().ToArray();
        Assert.Equal(new[] { "baseline", "pointShadowZeroNdl", "underwaterMarchZero" }, schedule.Select(b => b.GetProperty("state").GetString()));
        Assert.Equal(new[] { 0, 72, 144 }, schedule.Select(b => b.GetProperty("startCallback").GetInt32()));
        Assert.Equal(new[] { 72, 144, 150 }, schedule.Select(b => b.GetProperty("endCallbackExclusive").GetInt32()));
        Assert.Equal(new[] { 0, 1, 2 }, schedule.Select(b => b.GetProperty("gates").GetInt32()));
        int[] applied = report.GetProperty("appliedPerCallback").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        Assert.Equal(150, applied.Length);
        Assert.Equal((0, -1, 0, 1, 4, 1, 2), (applied[0], applied[3], applied[71], applied[72], applied[80], applied[81], applied[144]));
        JsonElement[] counts = report.GetProperty("counts").EnumerateArray().ToArray();
        Assert.Equal(new[] { "baseline", "pointShadowZeroNdl", "underwaterMarchZero", "rigidWorldReuse" },
            counts.Select(c => c.GetProperty("state").GetString()));
        Assert.Equal((72, 71, 1, 0), Counts(counts[0]));
        Assert.Equal((72, 71, 0, 1), Counts(counts[1]));
        Assert.Equal((6, 6, 0, 0), Counts(counts[2]));
        Assert.Equal((0, 0, 0, 0), Counts(counts[3]));
        Assert.Equal(4, json.RootElement.GetProperty("gpuSummary").GetProperty("shaderGateStates").GetArrayLength());
    }

    static (int, int, int, int) Counts(JsonElement c) => (c.GetProperty("scheduledCallbacks").GetInt32(),
        c.GetProperty("matchingCallbacks").GetInt32(), c.GetProperty("noFrameCallbacks").GetInt32(),
        c.GetProperty("mismatchedCallbacks").GetInt32());

    [Fact]
    public void State_summary_splits_frames_raw_spans_and_exclusive_time_by_block()
    {
        var gates = new FrameDiagnosticsShaderGateSchedule(null, 80);
        gates.Boundary(0, 0);
        gates.Boundary(72, 72);
        gates.End(80, 80);
        FrameDiagnosticsShaderGateReport report = gates.Report(80);
        var labels = PassLabelsSnapshot.Unavailable() with { Labels = ["shadow.directional", FrameDiagnosticsShaderGateSummary.MainPassLabel] };
        // 1 microsecond a tick, so 1000 ticks is 1 ms.
        var passes = PassTimingSnapshot.Unavailable() with
        {
            Supported = true,
            ClockPairs = new([new(0, 0, 0, 0, 1_000_000, 100), new(80, 80, 80, 80, 1_001_000_000, 1_000_100)], 0, 0),
            Encoders = new([
                // A settled baseline callback. Half of the main fragment overlaps another pass.
                new(1, 10, 0, "render", 0, 1000, 2000, 2000, 4000, null, null, "valid", 1),
                new(1, 10, 1, "render", 1, 3000, 3000, 3000, 4000, null, null, "valid", 0),
                // A baseline edge callback: a raw span but no exclusive share.
                new(2, 1, 0, "render", 0, 10000, 12000, 12000, 15000, null, null, "valid", 1),
                // The gate's block, settled. A smaller main pass.
                new(3, 75, 0, "render", 0, 20000, 20500, 20500, 21000, null, null, "valid", 1),
                // An invalid record counts nowhere.
                new(4, 76, 0, "render", 0, 30000, 20000, 90000, 80000, null, null, "invalid", 1)], 0, 0),
        };
        var submits = new[]
        {
            new FrameTimingSubmit(1, 8, 1, 10, FrameTimingCommandKind.FrameRecording),
            new FrameTimingSubmit(2, 8, 2, 11, FrameTimingCommandKind.FrameRecording),
            new FrameTimingSubmit(3, 8, 3, 71, FrameTimingCommandKind.FrameRecording),
            new FrameTimingSubmit(4, 8, 4, 72, FrameTimingCommandKind.FrameRecording),
            new FrameTimingSubmit(5, 8, 5, 73, FrameTimingCommandKind.FrameRecording),
        };
        var completions = new[]
        {
            new FrameTimingCompletion(1, 8, 11, 4, 1, 1.001),
            new FrameTimingCompletion(2, 8, 12, 4, 1.002, 1.004),
            new FrameTimingCompletion(3, 8, 13, 4, 2, 2.001),
            new FrameTimingCompletion(4, 8, 14, 4, 2.002, 2.005),   // Crosses the block boundary.
            new FrameTimingCompletion(5, 8, 15, 4, 2.006, 2.010),
        };
        var timing = new FrameTimingSnapshot(new(submits, 0, 0), new(completions, 0, 0), new([], 0, 0), new([], 0, 0), 0);

        var states = FrameDiagnosticsShaderGateSummary.States(timing, passes, labels, report);

        var baseline = states.Single(s => s.State == "baseline");
        var gate = states.Single(s => s.State == "pointShadowZeroNdl");
        Assert.Equal(4, states.Length);
        Assert.Equal(1, baseline.FrameCompletionIntervalMs.Count);
        Assert.Equal(3, baseline.FrameCompletionIntervalMs.Mean, 8);
        Assert.Equal(1, gate.FrameCompletionIntervalMs.Count);
        Assert.Equal(5, gate.FrameCompletionIntervalMs.Mean, 8);
        Assert.Equal(2, baseline.MainPassVertexMs.Count);
        Assert.Equal(1.5, baseline.MainPassVertexMs.Mean, 8);
        Assert.Equal(2.5, baseline.MainPassFragmentMs.Mean, 8);
        Assert.Equal(0.5, gate.MainPassVertexMs.Mean, 8);
        Assert.Equal(1, baseline.SettledCallbacks);
        Assert.Equal(1.0, baseline.MainPassExclusiveVertexMs.Mean, 8);
        Assert.Equal(1.5, baseline.MainPassExclusiveFragmentMs.Mean, 8);
        Assert.Equal(3.0, baseline.AttributedBusyMs.Mean, 8);
        Assert.Equal(0.5, gate.MainPassExclusiveFragmentMs.Mean, 8);
        Assert.Equal(0, states.Single(s => s.State == "rigidWorldReuse").MainPassVertexMs.Count);
    }
}
