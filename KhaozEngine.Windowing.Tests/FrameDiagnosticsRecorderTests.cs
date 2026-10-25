using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

[CollectionDefinition("AllocSensitive", DisableParallelization = true)]
public sealed class FrameDiagnosticsAllocationCollection { }

[Collection("AllocSensitive")]
public sealed class FrameDiagnosticsRecorderTests : IDisposable
{
    readonly string _directory = Path.Combine("/tmp/khaozengine-mac-frame-performance/release-diagnostics", "recorder-tests-" + Guid.NewGuid().ToString("N"));
    string ResultPath => Path.Combine(_directory, "result.json");
    static FrameDiagnosticsWindowState Foreground => new(true, false, false, 800, 600, 0, false);

    public FrameDiagnosticsRecorderTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    static GpuDeviceCounters Counters(long n) => new(n, n * 2, n * 3, n * 4, n * 5, n * 6, n * 7, n * 8, n * 9);

    FrameDiagnosticsRecorder Recorder(int capacity = 4096) => new(ResultPath, 1000, capacity);
    void Trigger() => File.WriteAllText(ResultPath + ".start", "");

    static void Observe(FrameDiagnosticsRecorder recorder, long timestamp, long count,
        double dt = 0.25, FrameDiagnosticsWindowState? window = null) =>
        recorder.Observe(dt, timestamp, window ?? Foreground, Counters(count));

    [Fact]
    public async Task Trigger_io_and_initial_partial_interval_are_excluded_and_previous_frame_counters_align()
    {
        using var recorder = Recorder();
        Trigger();
        Observe(recorder, 100, 100, 99);
        Assert.False(File.Exists(ResultPath + ".start"));
        Observe(recorder, 2000, 200, 88);
        Observe(recorder, 2300, 201, 0.3);
        Assert.False(File.Exists(ResultPath));
        Observe(recorder, 12000, 202, 9.7);
        await recorder.Publication;
        using var json = JsonDocument.Parse(File.ReadAllText(ResultPath));
        var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("sampleCount").GetInt32());
        Assert.Equal(10, root.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(2000, root.GetProperty("baseline").GetProperty("timestamp").GetInt64());
        var sample = root.GetProperty("samples")[0];
        Assert.Equal(2000, sample.GetProperty("startTimestamp").GetInt64());
        Assert.Equal(2300, sample.GetProperty("endTimestamp").GetInt64());
        Assert.Equal(0.3, sample.GetProperty("monotonicIntervalSeconds").GetDouble());
        Assert.Equal(0.3, sample.GetProperty("loopDeltaSeconds").GetDouble());
        Assert.Equal(201, sample.GetProperty("counters").GetProperty("framesBegun").GetInt64());
        Assert.Equal(200, root.GetProperty("baseline").GetProperty("counters").GetProperty("framesBegun").GetInt64());
        Assert.Equal(202, root.GetProperty("end").GetProperty("counters").GetProperty("framesBegun").GetInt64());
    }

    [Fact]
    public async Task Ten_second_boundary_is_inclusive_and_dt_is_never_clamped()
    {
        using var recorder = Recorder();
        Trigger();
        Observe(recorder, 0, 0);
        Observe(recorder, 100, 1);
        Observe(recorder, 10099, 2, 9.999);
        Assert.False(File.Exists(ResultPath));
        Observe(recorder, 10100, 3, 0.001);
        await recorder.Publication;
        using var json = JsonDocument.Parse(File.ReadAllText(ResultPath));
        Assert.Equal("duration", json.RootElement.GetProperty("stopReason").GetString());
        Assert.Equal(9.999, json.RootElement.GetProperty("samples")[0].GetProperty("loopDeltaSeconds").GetDouble());
        Assert.False(json.RootElement.GetProperty("summary").GetProperty("bufferCapacityReached").GetBoolean());
    }

    [Fact]
    public async Task Capacity_is_bounded_and_reports_a_short_capture_without_restarting()
    {
        using var recorder = Recorder(2);
        Trigger();
        Observe(recorder, 0, 0);
        Observe(recorder, 1, 1);
        Observe(recorder, 6, 2);
        Observe(recorder, 16, 3);
        await recorder.Publication;
        string original = File.ReadAllText(ResultPath);
        Trigger();
        Observe(recorder, 20000, 4);
        Assert.Equal(original, File.ReadAllText(ResultPath));
        Assert.True(File.Exists(ResultPath + ".start"));
        using var json = JsonDocument.Parse(original);
        Assert.Equal(2, json.RootElement.GetProperty("sampleCount").GetInt32());
        Assert.Equal("capacity", json.RootElement.GetProperty("stopReason").GetString());
        Assert.True(json.RootElement.GetProperty("summary").GetProperty("bufferCapacityReached").GetBoolean());
    }

    [Fact]
    public async Task Summary_uses_summed_monotonic_intervals_and_exact_counter_deltas()
    {
        using var recorder = Recorder(4);
        Trigger();
        Observe(recorder, 0, 0);
        Observe(recorder, 1000, 10);
        Observe(recorder, 1005, 11, 1);
        Observe(recorder, 1010, 12, 1);
        Observe(recorder, 1020, 13, 1);
        Observe(recorder, 1040, 14, 1);
        await recorder.Publication;
        using var json = JsonDocument.Parse(File.ReadAllText(ResultPath));
        var summary = json.RootElement.GetProperty("summary");
        Assert.Equal(100, summary.GetProperty("framesPerSecond").GetDouble());
        Assert.Equal(10, summary.GetProperty("meanFrameMs").GetDouble());
        Assert.Equal(7.5, summary.GetProperty("medianFrameMs").GetDouble());
        Assert.Equal(20, summary.GetProperty("p95FrameMs").GetDouble());
        Assert.Equal(20, summary.GetProperty("p99FrameMs").GetDouble());
        Assert.Equal(20, summary.GetProperty("worstFrameMs").GetDouble());
        Assert.Equal(0.5, summary.GetProperty("shareWithin144HzBudget").GetDouble());
        var delta = summary.GetProperty("counterDelta");
        Assert.Equal(4, delta.GetProperty("framesBegun").GetInt64());
        Assert.Equal(8, delta.GetProperty("drainCount").GetInt64());
        Assert.Equal(12, delta.GetProperty("drainMs").GetDouble());
        Assert.Equal(16, delta.GetProperty("backpressureStallCount").GetInt64());
        Assert.Equal(20, delta.GetProperty("backpressureStallMs").GetDouble());
        Assert.Equal(32, delta.GetProperty("acquireWaitCount").GetInt64());
        Assert.Equal(36, delta.GetProperty("acquireWaitMs").GetDouble());
        Assert.Equal(2, delta.GetProperty("drainCountPerInterval").GetDouble());
        Assert.Equal(3, delta.GetProperty("drainMsPerInterval").GetDouble());
        Assert.Equal(4, delta.GetProperty("backpressureStallCountPerInterval").GetDouble());
        Assert.Equal(5, delta.GetProperty("backpressureStallMsPerInterval").GetDouble());
        Assert.Equal(8, delta.GetProperty("acquireWaitCountPerInterval").GetDouble());
        Assert.Equal(9, delta.GetProperty("acquireWaitMsPerInterval").GetDouble());
        Assert.True(summary.GetProperty("allForegroundRenderedStableSize").GetBoolean());
        Assert.False(summary.GetProperty("cadenceCapped").GetBoolean());
    }

    [Fact]
    public async Task Tail_percentiles_use_nearest_rank_and_sampling_does_not_poll_a_new_trigger()
    {
        using var recorder = Recorder(20);
        Trigger();
        Observe(recorder, 0, 0);
        Observe(recorder, 1000, 10);
        // A directory trigger would fail consumption. During sampling it must remain untouched.
        Directory.CreateDirectory(ResultPath + ".start");
        long timestamp = 1000;
        for (int i = 1; i <= 20; i++)
        {
            timestamp += i;
            Observe(recorder, timestamp, 10 + i);
        }
        await recorder.Publication;
        using var json = JsonDocument.Parse(File.ReadAllText(ResultPath));
        var summary = json.RootElement.GetProperty("summary");
        Assert.Equal(10.5, summary.GetProperty("medianFrameMs").GetDouble());
        Assert.Equal(19, summary.GetProperty("p95FrameMs").GetDouble());
        Assert.Equal(20, summary.GetProperty("p99FrameMs").GetDouble());
        Assert.Equal(0.3, summary.GetProperty("shareWithin144HzBudget").GetDouble());
        Assert.True(Directory.Exists(ResultPath + ".start"));
        Assert.Null(recorder.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sampling_boundaries_allocate_nothing_after_warmup(bool rendererInitializesSwapchain)
    {
        using var recorder = Recorder();
        var window = Foreground with { RendererInitializesSwapchain = rendererInitializesSwapchain };
        Trigger();
        Observe(recorder, 0, 0);
        Observe(recorder, 1000, 1, window: window);
        for (int i = 1; i <= 16; i++) Observe(recorder, 1000 + i, i + 1, window: window);
        long bytes;
        using (AllocationRegion.Start("direct diagnostics sampling boundaries"))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 17; i <= 32; i++) Observe(recorder, 1000 + i, i + 1, window: window);
            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(0, bytes);
        Observe(recorder, 11000, 34, window: window);
        await recorder.Publication;
        Assert.Null(recorder.Failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Unsupported_counters_are_absent_from_summary_even_if_only_one_boundary_lacks_them(int unsupportedBoundary)
    {
        using var recorder = Recorder(2);
        Trigger();
        Observe(recorder, 0, 0);
        recorder.Observe(0.1, 100, Foreground, unsupportedBoundary == 0 ? default : Counters(1));
        recorder.Observe(0.1, 200, Foreground, unsupportedBoundary == 1 ? default : Counters(2));
        recorder.Observe(0.1, 300, Foreground, unsupportedBoundary == 2 ? default : Counters(3));
        await recorder.Publication;
        using var json = JsonDocument.Parse(File.ReadAllText(ResultPath));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("summary").GetProperty("counterDelta").ValueKind);
        var boundary = unsupportedBoundary == 1 ? json.RootElement.GetProperty("samples")[0]
            : json.RootElement.GetProperty(unsupportedBoundary == 0 ? "baseline" : "end");
        Assert.False(boundary.GetProperty("counters").GetProperty("hasValue").GetBoolean());
    }

    [Theory]
    [InlineData(false, false, false, 800, 600, 0, false)]
    [InlineData(true, true, true, 800, 600, 0, false)]
    [InlineData(true, false, false, 801, 600, 0, false)]
    [InlineData(true, false, true, 800, 600, 0, false)]
    public async Task Focus_minimize_suppression_and_resize_invalidate_a_comparison(bool focused, bool minimized,
        bool suppressed, int width, int height, int cap, bool vsync)
    {
        using var recorder = Recorder(2);
        Trigger();
        Observe(recorder, 0, 0);
        Observe(recorder, 100, 1);
        var changed = new FrameDiagnosticsWindowState(focused, minimized, suppressed, width, height, cap, vsync);
        Observe(recorder, 200, 2, window: changed);
        Observe(recorder, 300, 3);
        await recorder.Publication;
        using var json = JsonDocument.Parse(File.ReadAllText(ResultPath));
        Assert.False(json.RootElement.GetProperty("summary").GetProperty("allForegroundRenderedStableSize").GetBoolean());
        Assert.Equal(focused, json.RootElement.GetProperty("samples")[1].GetProperty("completedFrameWindow").GetProperty("focused").GetBoolean());
    }

    [Theory]
    [InlineData(144, false)]
    [InlineData(0, true)]
    public async Task Base_cap_or_vsync_marks_capture_capped(int cap, bool vsync)
    {
        using var recorder = Recorder(1);
        Trigger();
        Observe(recorder, 0, 0);
        Observe(recorder, 100, 1, window: Foreground with { EffectiveBaseCapHz = cap, Vsync = vsync });
        Observe(recorder, 200, 2);
        await recorder.Publication;
        using var json = JsonDocument.Parse(File.ReadAllText(ResultPath));
        Assert.True(json.RootElement.GetProperty("summary").GetProperty("cadenceCapped").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_swapchain_mode_is_serialized_at_baseline_end_and_both_sample_boundaries(bool rendererOwned)
    {
        using var recorder = Recorder(1);
        var window = Foreground with { RendererInitializesSwapchain = rendererOwned };
        Trigger();
        Observe(recorder, 0, 0);
        Observe(recorder, 100, 1, window: window);
        Observe(recorder, 200, 2, window: window);
        await recorder.Publication;
        using var json = JsonDocument.Parse(File.ReadAllText(ResultPath));
        var root = json.RootElement;
        Assert.Equal(rendererOwned, root.GetProperty("baseline").GetProperty("window").GetProperty("rendererInitializesSwapchain").GetBoolean());
        Assert.Equal(rendererOwned, root.GetProperty("end").GetProperty("window").GetProperty("rendererInitializesSwapchain").GetBoolean());
        var sample = root.GetProperty("samples")[0];
        Assert.Equal(rendererOwned, sample.GetProperty("completedFrameWindow").GetProperty("rendererInitializesSwapchain").GetBoolean());
        Assert.Equal(rendererOwned, sample.GetProperty("boundaryWindow").GetProperty("rendererInitializesSwapchain").GetBoolean());
        Assert.True(root.GetProperty("summary").GetProperty("allForegroundRenderedStableSize").GetBoolean());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Any_observed_swapchain_mode_change_invalidates_comparison_including_a_return_to_baseline(
        bool initialMode, bool changeOnlyAtEnd)
    {
        using var recorder = Recorder(2);
        var baseline = Foreground with { RendererInitializesSwapchain = initialMode };
        var changed = baseline with { RendererInitializesSwapchain = !initialMode };
        Trigger();
        Observe(recorder, 0, 0);
        Observe(recorder, 100, 1, window: baseline);
        Observe(recorder, 200, 2, window: changeOnlyAtEnd ? baseline : changed);
        Observe(recorder, 300, 3, window: changeOnlyAtEnd ? changed : baseline);
        await recorder.Publication;
        using var json = JsonDocument.Parse(File.ReadAllText(ResultPath));
        var root = json.RootElement;
        Assert.False(root.GetProperty("summary").GetProperty("allForegroundRenderedStableSize").GetBoolean());
        var sample = root.GetProperty("samples")[1];
        Assert.Equal(changeOnlyAtEnd ? initialMode : !initialMode,
            sample.GetProperty("completedFrameWindow").GetProperty("rendererInitializesSwapchain").GetBoolean());
        Assert.Equal(changeOnlyAtEnd ? !initialMode : initialMode,
            sample.GetProperty("boundaryWindow").GetProperty("rendererInitializesSwapchain").GetBoolean());
    }

    [Fact]
    public void Polling_waits_half_a_second_and_early_close_never_publishes()
    {
        var recorder = Recorder();
        Observe(recorder, 0, 0);
        Trigger();
        Observe(recorder, 499, 1);
        Assert.True(File.Exists(ResultPath + ".start"));
        Observe(recorder, 500, 2);
        Assert.False(File.Exists(ResultPath + ".start"));
        Observe(recorder, 501, 3);
        Observe(recorder, 502, 4);
        recorder.Dispose();
        Assert.False(File.Exists(ResultPath));
        Assert.NotNull(recorder.Failure);
    }

    [Fact]
    public void Disposal_joins_started_publication_and_metadata_contains_only_assembly_identity()
    {
        var recorder = Recorder(1);
        Trigger();
        Observe(recorder, 0, 0);
        Observe(recorder, 100, 1);
        Observe(recorder, 200, 2);
        recorder.Dispose();
        Assert.True(recorder.Publication.IsCompletedSuccessfully);
        Assert.Null(recorder.Failure);
        using var json = JsonDocument.Parse(File.ReadAllText(ResultPath));
        Assert.Equal(3, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Environment.ProcessId, json.RootElement.GetProperty("processId").GetInt32());
        Assert.Equal(1000, json.RootElement.GetProperty("stopwatchFrequency").GetInt64());
        Assert.Contains("KhaozEngine.Windowing", json.RootElement.GetProperty("engineAssembly").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("appAssembly").GetString()));
        Assert.False(json.RootElement.TryGetProperty("environment", out _));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Existing_result_is_refused_at_start_and_atomic_publication_never_overwrites_a_late_result()
    {
        File.WriteAllText(ResultPath, "existing");
        Assert.Throws<IOException>(() => Recorder());
        File.Delete(ResultPath);
        using var recorder = Recorder(1);
        Trigger();
        Observe(recorder, 0, 0);
        Observe(recorder, 100, 1);
        File.WriteAllText(ResultPath, "raced");
        Observe(recorder, 200, 2);
        await recorder.Publication;
        Assert.Equal("raced", File.ReadAllText(ResultPath));
        Assert.NotNull(recorder.Failure);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Relative_paths_are_rejected() => Assert.Throws<ArgumentException>(() => new FrameDiagnosticsRecorder("result.json", 1000));
}
