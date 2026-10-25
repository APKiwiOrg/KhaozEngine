using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

[Collection("AllocSensitive")]
public sealed class PreparePhaseDiagnosticsTests : IDisposable
{
    readonly string _directory = Path.Combine("/tmp/khaozengine-mac-frame-performance/prepare-split-probe",
        "recorder-unit-" + Guid.NewGuid().ToString("N"));
    public PreparePhaseDiagnosticsTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    static FrameDiagnosticsWindowState Window(bool active, long prepares, long composites) =>
        new(true, false, false, 800, 600, 0, true, true, active, prepares, composites);

    [Theory]
    [InlineData(false, 5, 5, 5, 5, true)]
    [InlineData(true, 5, 6, 5, 6, true)]
    [InlineData(true, 5, 6, 5, 5, false)]
    [InlineData(true, 5, 7, 5, 6, false)]
    [InlineData(false, 5, 6, 5, 6, false)]
    public void Actual_submissions_and_composites_must_pair_with_every_interval(bool active,
        long startPrepares, long endPrepares, long startComposites, long endComposites, bool expected)
    {
        var startWindow = Window(active, startPrepares, startComposites);
        var endWindow = Window(active, endPrepares, endComposites);
        var start = new FrameDiagnosticsBoundary(1000, startWindow, default);
        var end = new FrameDiagnosticsBoundary(2000, endWindow, default);
        FrameDiagnosticsSample[] samples = [new(1000, 2000, 1, 1, startWindow, endWindow, default)];
        var report = FrameDiagnosticsReport.Create(1000, start, end, samples, 1, "duration");
        Assert.Equal(3, report.SchemaVersion);
        Assert.Equal(expected, report.Summary.AllPreparePhaseSubmissionsPaired);
    }

    [Fact]
    public void A_mode_change_invalidates_the_sample_even_when_the_window_is_unchanged()
    {
        var startWindow = Window(false, 0, 0);
        var endWindow = Window(true, 1, 1);
        var start = new FrameDiagnosticsBoundary(1000, startWindow, default);
        var end = new FrameDiagnosticsBoundary(2000, endWindow, default);
        FrameDiagnosticsSample[] samples = [new(1000, 2000, 1, 1, startWindow, endWindow, default)];
        var report = FrameDiagnosticsReport.Create(1000, start, end, samples, 1, "duration");
        Assert.False(report.Summary.AllForegroundRenderedStableSize);
        Assert.False(report.Summary.AllPreparePhaseSubmissionsPaired);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_modes_record_actual_counts_without_warmed_sampling_allocations(bool active)
    {
        string path = Path.Combine(_directory, "result.json");
        using var recorder = new FrameDiagnosticsRecorder(path, 1000);
        File.WriteAllText(path + ".start", "");
        recorder.Observe(0.001, 0, Window(active, 0, 0), default);
        recorder.Observe(0.001, 1000, Window(active, 1, 1), default);
        for (int i = 1; i <= 16; i++)
            recorder.Observe(0.001, 1000 + i, Window(active, active ? i + 1 : 1, active ? i + 1 : 1), default);
        long bytes;
        using (AllocationRegion.Start("prepare-phase diagnostics boundaries"))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 17; i <= 32; i++)
                recorder.Observe(0.001, 1000 + i, Window(active, active ? i + 1 : 1, active ? i + 1 : 1), default);
            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(0, bytes);
        recorder.Observe(9.968, 11000, Window(active, active ? 34 : 1, active ? 34 : 1), default);
        await recorder.Publication;
        using var report = JsonDocument.Parse(File.ReadAllText(path));
        var root = report.RootElement;
        Assert.Equal(active, root.GetProperty("baseline").GetProperty("window").GetProperty("preparePhaseSplitActive").GetBoolean());
        Assert.Equal(active ? 34 : 1, root.GetProperty("end").GetProperty("window").GetProperty("preparePhaseSubmissionCount").GetInt64());
        Assert.True(root.GetProperty("summary").GetProperty("allPreparePhaseSubmissionsPaired").GetBoolean());
    }

}
