using System;
using System.IO;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
using Xunit;
using static KhaozEngine.Tests.Windowing.RecordingWaitProbeTestData;

namespace KhaozEngine.Tests.Windowing;

public sealed partial class RecordingWaitProbeRecorderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Only_enabled_armed_or_sampling_recorder_reads_the_capability(bool enabled)
    {
        using var r = new FrameDiagnosticsRecorder(Pathname, 1000, 1, ShadowStageProbeMode.Counts, enabled);
        var source = new SnapshotSource();
        Assert.Equal(RecordingWaitBoundaryStatus.Off, r.ReadRecordingWaitBoundary(source).Status);
        Assert.Equal(0, source.Reads);
        File.WriteAllText(Pathname + ".start", "");
        r.Observe(.016, 0, Window(), default);
        Assert.Equal(enabled ? RecordingWaitBoundaryStatus.Available : RecordingWaitBoundaryStatus.Off, r.ReadRecordingWaitBoundary(source).Status);
        Assert.Equal(enabled ? 1 : 0, source.Reads);
    }

    [Theory]
    [InlineData("unsupported", RecordingWaitBoundaryStatus.Unsupported)]
    [InlineData("throw", RecordingWaitBoundaryStatus.Fault)]
    [InlineData("contended", RecordingWaitBoundaryStatus.Contended)]
    [InlineData("unavailable", RecordingWaitBoundaryStatus.Unavailable)]
    public void Missing_contended_and_throwing_sources_preserve_absence(string kind, RecordingWaitBoundaryStatus expected)
    {
        using var r = new FrameDiagnosticsRecorder(Pathname, 1000, 1, ShadowStageProbeMode.Counts, true);
        File.WriteAllText(Pathname + ".start", ""); r.Observe(.016, 0, Window(), default);
        var source = new SnapshotSource { Kind = kind };
        var b = r.ReadRecordingWaitBoundary(kind == "unsupported" ? null : source);
        Assert.Equal(expected, b.Status); Assert.Null(b.Totals);
        Assert.Equal(kind == "unsupported" ? 0 : 1, source.Reads);
    }

    sealed class SnapshotSource : IGpuRingWaitSource
    {
        internal int Reads;
        internal string? Kind { get; init; }
        public Guid RingWaitDeviceIdentity => Device;
        public GpuRingWaitSnapshotStatus TryReadRingWaitTotals(out GpuRingWaitTotals totals)
        {
            Reads++;
            if (Kind == "throw") throw new InvalidOperationException("diagnostic");
            totals = new(Device, 2, 1000, 0, 0);
            return Kind switch { "contended" => GpuRingWaitSnapshotStatus.Contended, "unavailable" => GpuRingWaitSnapshotStatus.Unavailable, _ => GpuRingWaitSnapshotStatus.Available };
        }
    }
}
