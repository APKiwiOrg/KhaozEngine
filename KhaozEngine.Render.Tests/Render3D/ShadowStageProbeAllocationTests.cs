using System;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class ShadowStageProbeAllocationTests
{
    [Theory]
    [InlineData(ShadowStageProbeMode.Off)]
    [InlineData(ShadowStageProbeMode.Counts)]
    [InlineData(ShadowStageProbeMode.Timings)]
    public async Task Warmed_producer_frame_and_recorder_sampling_allocate_zero_bytes(ShadowStageProbeMode mode)
    {
        string directory = Path.Combine("/tmp/khaozengine-mac-frame-performance/shadow-stage-probe", "alloc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var f = new ShadowStageProbeFixture();
            f.Render(ShadowStageProbeMode.Off);
            f.Render(ShadowStageProbeMode.Off);
            long clockReads = 0;
            f.Scene.SetShadowStageProbeClock(() => clockReads++, 1000);
            string path = Path.Combine(directory, "result.json");
            using var recorder = new FrameDiagnosticsRecorder(path, 1000, 128, mode);
            var window = new FrameDiagnosticsWindowState(true, false, false, 32, 24, 0, false);
            File.WriteAllText(path + ".start", "");
            recorder.Observe(.016, 0, window, default);
            recorder.Observe(.016, 1000, window, default);
            using var commands = f.Device.Factory.CreateCommandList();
            commands.Begin();
            long timestamp = 1000;
            void Sample(long boundary)
            {
                f.Frame.BeginShadowStageCallback(recorder.NextShadowStageRequest);
                f.Scene.ConfigureShadowStageProbe(f.Frame.ShadowStageRequest);
                f.Scene.PreparePointShadows(commands, f.Scene.Camera.Eye);
                f.Scene.PublishShadowStageProbe(f.Frame);
                f.Frame.CompleteShadowStageCallback();
                recorder.Observe(.016, boundary, window, default, f.Frame.CompletedShadowStageCallback);
            }
            for (int i = 0; i < 16; i++) Sample(timestamp += 16);
            AllocationMeasurement.NoPerCallAllocation("warmed shadow producer plus Frame and recorder", () =>
            {
                for (int i = 0; i < 16; i++) Sample(timestamp += 16);
            });
            if (mode != ShadowStageProbeMode.Timings) Assert.Equal(0, clockReads);
            else Assert.True(clockReads > 0);
            if (mode != ShadowStageProbeMode.Off)
                Assert.Equal(1, f.Frame.CompletedShadowStageCallback.Snapshot!.Value.Counters!.Value.StaticRequests);
            // The stop boundary, writer scheduling, JSON and join are deliberately outside the region.
            Sample(11000);
            commands.End();
            await recorder.Publication;
            Assert.Null(recorder.Failure);
        }
        finally { Directory.Delete(directory, true); }
    }
}
