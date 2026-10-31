using System;
using System.IO;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class PointShadowAttributionAllocationTests
{
    [Fact]
    public void Warmed_producer_frame_and_recorder_retain_new_ranges_without_allocating()
    {
        string directory = Path.Combine(Path.GetTempPath(), "khaozengine-point-attribution-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using var f = new ShadowStageProbeFixture();
            f.Render(ShadowStageProbeMode.Off); f.Render(ShadowStageProbeMode.Off);
            string path = Path.Combine(directory, "result.json");
            using var recorder = new FrameDiagnosticsRecorder(path, 1000, 128, ShadowStageProbeMode.Counts, pointAttribution: true);
            var window = new FrameDiagnosticsWindowState(true, false, false, 32, 24, 0, false);
            File.WriteAllText(path + ".start", "");
            recorder.Observe(.016, 0, window, default); recorder.Observe(.016, 1000, window, default);
            using var commands = f.Device.Factory.CreateCommandList();
            commands.Begin();
            long timestamp = 1000;
            void Sample()
            {
                f.Frame.BeginShadowStageCallback(recorder.NextShadowStageRequest);
                f.Scene.ConfigureShadowStageProbe(f.Frame.ShadowStageRequest);
                f.Scene.PreparePointShadows(commands, f.Scene.Camera.Eye);
                f.Scene.PublishShadowStageProbe(f.Frame); f.Frame.CompleteShadowStageCallback();
                recorder.Observe(.016, timestamp += 16, window, default, f.Frame.CompletedShadowStageCallback);
            }
            for (int i = 0; i < 4; i++) Sample();
            AllocationMeasurement.NoPerCallAllocation("point attribution producer, Frame and recorder", () =>
            {
                for (int i = 0; i < 8; i++) Sample();
            });
            commands.End();
            var capture = recorder.NextShadowStageRequest.PointAttribution!;
            Assert.Equal(12, capture.FrameCount); Assert.Equal(PointAttributionCapacity.None, capture.Overflow);
            recorder.Dispose();
            Assert.True(capture.Frozen);
            Assert.Equal(12, capture.FrameCount);
        }
        finally { Directory.Delete(directory, true); }
    }
}
