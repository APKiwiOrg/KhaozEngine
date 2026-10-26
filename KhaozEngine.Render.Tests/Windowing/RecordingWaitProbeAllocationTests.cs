using System;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.Windowing;
using Xunit;
using static KhaozEngine.Tests.Windowing.RecordingWaitProbeTestData;

namespace KhaozEngine.Tests.Windowing;

[Collection("AllocSensitive")]
public sealed class RecordingWaitProbeAllocationTests
{
    [Fact]
    public async Task Warmed_observation_value_copy_and_sampling_allocate_zero_bytes()
    {
        string directory = Path.Combine("/tmp/khaozengine-mac-frame-performance/ring-wait-probe/implementation", "allocation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var f = new RecordingWaitProbeFixture();
            string path = Path.Combine(directory, "result.json");
            using var r = new FrameDiagnosticsRecorder(path, 1000, 128, ShadowStageProbeMode.Counts, true);
            File.WriteAllText(path + ".start", "");
            r.Observe(.016, 0, Window(0, false), default);
            r.Observe(.016, 1000, Window(0, false), default, default, default, Boundary());
            long t = 1000;
            void Sample()
            {
                f.Frame.BeginRecordingWaitCallback(r.NextRecordingWaitRequest);
                f.Frame.DeclareRecordingWaitPrefix(false); f.Observe(RecordingWaitRole.Main);
                f.Frame.CompleteRecordingWaitCallback();
                r.Observe(.016, ++t, Window(0, false), default, default, f.Frame.CompletedRecordingWaitCallback,
                    r.ReadRecordingWaitBoundary(f.MainSource));
            }
            for (int i = 0; i < 16; i++) Sample();
            AllocationMeasurement.NoPerCallAllocation("recording receipts, Frame copy and ring recorder", () =>
            { for (int i = 0; i < 16; i++) Sample(); });
            Assert.True(f.Frame.CompletedRecordingWaitCallback.Main.Receipt.HasValue);
            t = 10999; Sample(); await r.Publication; Assert.Null(r.Failure);
        }
        finally { Directory.Delete(directory, true); }
    }
}
