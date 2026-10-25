using System;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Tests.Windowing;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class ShadowStageProbeCallbackTests
{
    [Theory]
    [InlineData("ui")]
    [InlineData("capture")]
    [InlineData("submit")]
    [InlineData("after-submit")]
    [InlineData("present")]
    [InlineData("pacing")]
    [InlineData("missing-acknowledgement")]
    public async Task A_recorded_composite_cannot_acknowledge_a_late_failed_or_abandoned_callback(string phase)
    {
        string directory = Path.Combine("/tmp/khaozengine-mac-frame-performance/shadow-stage-probe", "callback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var gpu = new FakeGpuDevice(GpuBackendKind.MetalNative);
            using var output = new PostChainRig.Output(gpu, 32, 24);
            using var device = new FrameOutputDevice(gpu, output);
            using var scene = new Scene3D(device, output.Framebuffer.Outputs);
            using var prepared = new PreparePhaseSceneSubmission(device, scene);
            using var commands = device.Factory.CreateCommandList();
            using var taps = new FrameCaptureTaps(device, null);
            var frame = new Frame { Commands = commands, Width = 32, Height = 24 };
            var original = new InvalidOperationException("planned " + phase);
            string path = Path.Combine(directory, "result.json");
            using var recorder = new FrameDiagnosticsRecorder(path, 1000, 1, ShadowStageProbeMode.Counts);
            var window = new FrameDiagnosticsWindowState(true, false, false, 32, 24, 0, false);
            File.WriteAllText(path + ".start", "");
            recorder.Observe(.016, 0, window, default);
            recorder.Observe(.016, 1000, window, default);
            frame.BeginShadowStageCallback(recorder.NextShadowStageRequest);
            device.BeforeSubmit = list => { if (phase == "submit" && ReferenceEquals(list, commands)) throw original; };
            device.BeforePresent = () => { if (phase == "present") throw original; };
            taps.Add(new Tap(phase, original));
            void Callback()
            {
                RendererFramePolicy.Run(frame, true, device, commands, Color.Black,
                    f =>
                    {
                        scene.ConfigureShadowStageProbe(f.ShadowStageRequest);
                        scene.Begin(); scene.PrepareFrame(); prepared.Prepare(f);
                    },
                    f =>
                    {
                        Render3DSurface.RecordSceneOutput(scene, f, prepared, output.Framebuffer);
                        if (phase == "ui") throw original;
                    }, taps, true);
                if (phase == "pacing") throw original;
                if (phase != "missing-acknowledgement") frame.CompleteShadowStageCallback();
            }
            try
            {
                if (phase == "missing-acknowledgement") Callback();
                else Assert.Same(original, Assert.Throws<InvalidOperationException>(Callback));
                Assert.Equal(1, frame.PreparePhaseSubmissionCount);
                Assert.Equal(1, frame.PreparePhaseCompositeCount);
                Assert.NotNull(frame.CompletedShadowStageCallback.Snapshot);
                Assert.False(frame.CompletedShadowStageCallback.Completed);
                recorder.Observe(.016, 1016, window, default, frame.CompletedShadowStageCallback);
                await recorder.Publication;
                Assert.Null(recorder.Failure);
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                Assert.Equal((int)ShadowStageProbeStatus.IncompleteCallback,
                    json.RootElement.GetProperty("samples")[0].GetProperty("shadowStage").GetProperty("status").GetInt32());
                Assert.Equal(0, json.RootElement.GetProperty("shadowStages").GetProperty("validRows").GetInt32());
            }
            finally { frame.DisposeSubmission(); }
        }
        finally { Directory.Delete(directory, true); }
    }

    sealed class Tap(string phase, Exception original) : IFrameCaptureTap
    {
        public void Record(FrameCaptureContext context) { if (phase == "capture") throw original; }
        public void AfterSubmit(FrameCaptureContext context) { if (phase == "after-submit") throw original; }
        public void Detach(Action<IDisposable> retire) { }
    }
}
