using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

[Collection("AllocSensitive")]
public sealed class FrameDiagnosticsExtensionTests
{
    [Fact]
    public async Task Annotations_follow_completed_intervals_with_exact_float_bits_and_no_sampling_allocations()
    {
        string directory = Path.Combine("/tmp/khaozengine-mac-frame-performance/recorder-23-1-0", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "result.json");
            var annotation = new Annotation();
            using var recorder = new FrameDiagnosticsRecorder(path, 1000, 32, annotation);
            annotation.Channels[0] = "changed.after.registration";
            File.WriteAllText(path + ".start", "");
            var state = new FrameDiagnosticsWindowState(true, false, false, 800, 600, 0, true);
            Assert.False(MetalFrameTimingProbe.Sampling);
            recorder.Observe(0, 0, state, default);
            Assert.False(MetalFrameTimingProbe.Sampling);
            recorder.Observe(0, 1, state, default);
            Assert.True(MetalFrameTimingProbe.Sampling);
            for (int i = 2; i < 10; i++)
            {
                recorder.Observe(0.001, i, state, default);
            }
            annotation.Value = BitConverter.SingleToInt32Bits(-0.0f);
            long bytes;
            using (AllocationRegion.Start("diagnostic annotation sampling"))
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                recorder.Observe(0.001, 10, state, default);
                bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            Assert.Equal(0, bytes);
            recorder.Observe(10, 10001, state, default);
            await recorder.Publication;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var rows = json.RootElement.GetProperty("annotations");
            Assert.Equal("camera.test.bits", rows.GetProperty("channels")[0].GetString());
            Assert.Equal(json.RootElement.GetProperty("sampleCount").GetInt32(), rows.GetProperty("values").GetArrayLength());
            Assert.Equal(BitConverter.SingleToInt32Bits(-0.0f), rows.GetProperty("values")[8][0].GetInt64());
            Assert.False(MetalFrameTimingProbe.Sampling);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Gpu_and_present_summaries_use_native_seconds_and_report_missing_tail_coverage()
    {
        var submissions = new[]
        {
            new FrameTimingSubmit(1, 8, 110, 0, FrameTimingCommandKind.FrameRecording),
            new FrameTimingSubmit(1, 8, 210, 1, FrameTimingCommandKind.FrameRecording),
            new FrameTimingSubmit(3, 8, 310, 2, FrameTimingCommandKind.FrameRecording),
        };
        var completions = new[]
        {
            new FrameTimingCompletion(1, 8, 250, 4, 1.010, 1.016),
            new FrameTimingCompletion(1, 8, 150, 4, 1.000, 1.004),
        };
        var calls = new[] { new FrameTimingPresentCall(10, 100, 1, 120, 0), new FrameTimingPresentCall(11, 101, 2, 220, 1) };
        var presented = new[] { new FrameTimingPresented(10, 160, 1.020), new FrameTimingPresented(11, 260, 1.030), new FrameTimingPresented(12, 270, 0) };
        var summary = FrameDiagnosticsGpuSummary.Create(new FrameTimingSnapshot(
            new(submissions, 0, 0), new(completions, 0, 0), new(calls, 0, 0), new(presented, 1, 0), 0), 3);
        Assert.Equal(2, summary.FrameGpuBusyMs.Count);
        Assert.Equal(5, summary.FrameGpuBusyMs.Mean, 8);
        Assert.Equal(6, summary.GpuIdleGapMs.Mean, 8);
        Assert.Equal(10, summary.PresentedIntervalMs.Mean, 8);
        Assert.Equal(1, summary.UnpresentedDrawables);
        Assert.Equal(new[] { true, true, false }, summary.FrameCompletionCoverage);
        Assert.Equal(new[] { true, true, false }, summary.PresentCallCoverage);
        Assert.Equal(new[] { true, true, false }, summary.PresentedCoverage);
        Assert.False(summary.AllCallbacksCovered);
        Assert.True(summary.Overflowed);
    }

    [Fact]
    public void Reused_drawables_do_not_fill_an_earlier_missing_presentation_and_invalid_gpu_times_are_excluded()
    {
        var snapshot = new FrameTimingSnapshot(new([], 0, 0),
            new([new(1, 8, 150, 4, 0, 0), new(2, 8, 250, 5, 1, 2)], 0, 0),
            new([new(10, 100, 1, 120, 0), new(10, 100, 2, 220, 1)], 0, 0),
            new([new(10, 260, 1.030)], 0, 0), 0);
        var summary = FrameDiagnosticsGpuSummary.Create(snapshot, 2);
        Assert.Equal(new[] { false, true }, summary.PresentedCoverage);
        Assert.Equal(0, summary.FrameGpuBusyMs.Count);
        Assert.Equal(0, summary.GpuIdleGapMs.Count);
        Assert.Equal(2, summary.InvalidGpuTimings);
    }

    sealed class Annotation : IFrameDiagnosticsAnnotator
    {
        public string[] Channels { get; } = ["camera.test.bits"];
        internal long Value;
        public void Sample(Span<long> values) => values[0] = Value;
    }
}
