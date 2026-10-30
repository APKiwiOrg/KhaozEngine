using System;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;
using static KhaozEngine.Tests.Windowing.RecordingWaitProbeTestData;

namespace KhaozEngine.Tests.Windowing;

public sealed class RecordingWaitProbeCallbackTests
{
    [Theory]
    [InlineData("main-begin")]
    [InlineData("render")]
    [InlineData("capture")]
    [InlineData("end")]
    [InlineData("submit")]
    [InlineData("after-submit")]
    [InlineData("present")]
    [InlineData("pacing")]
    [InlineData("missing-ack")]
    public void Every_failure_after_prefix_submission_leaves_callback_unaccepted(string phase)
    {
        using var f = new RecordingWaitProbeFixture();
        using var taps = new FrameCaptureTaps(f.Device, null);
        var original = new InvalidOperationException(phase);
        taps.Add(new Tap(phase, original));
        f.Main.BeforeProbeBegin = () => { if (phase == "main-begin") throw original; };
        f.Main.AfterProbeEnd = () => { f.MainSource.End(); if (phase == "end") throw original; };
        f.Device.BeforeSubmit = list => { if (phase == "submit" && ReferenceEquals(list, f.Main)) throw original; };
        f.Device.BeforePresent = () => { if (phase == "present") throw original; };
        void Run()
        {
            RendererFramePolicy.Run(f.Frame, true, f.Device, f.Main, Color.Black,
                _ => f.PrefixSubmission(), _ => { if (phase == "render") throw original; f.Frame.RecordPreparePhaseComposite(); }, taps, true);
            if (phase == "pacing") throw original;
            if (phase != "missing-ack") f.Frame.CompleteRecordingWaitCallback();
        }
        if (phase == "missing-ack") Run();
        else Assert.Same(original, Assert.Throws<InvalidOperationException>(Run));
        Assert.Equal(1, f.Frame.PreparePhaseSubmissionCount);
        Assert.Equal(1, f.Frame.CompletedRecordingWaitCallback.PrefixObservations);
        Assert.Equal(0, f.Frame.CompletedRecordingWaitCallback.AcknowledgementCount);
        Assert.False(f.MainSource.Recording);
        Assert.Equal(RecordingWaitProbeStatus.IncompleteCallback,
            RecordingWaitProbeAccounting.Validate(Request(), f.Frame.CompletedRecordingWaitCallback, false));
        f.Main.BeforeProbeBegin = null;
        f.Main.AfterProbeEnd = f.MainSource.End;
        using var next = GpuRecording.Open(f.Device, f.Main, "failed callback released register");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frame_phases_observe_main_only_when_rendered(bool render)
    {
        using var f = new RecordingWaitProbeFixture();
        RendererFramePolicy.Run(f.Frame, render, f.Device, f.Main, Color.Black,
            _ => f.Frame.DeclareRecordingWaitPrefix(false), _ => { }, null, rendererInitializesSwapchain: true);
        f.Frame.CompleteRecordingWaitCallback();
        Assert.Equal(render ? 1 : 0, f.Frame.CompletedRecordingWaitCallback.MainObservations);
        Assert.Equal(render ? RecordingWaitProbeStatus.Valid : RecordingWaitProbeStatus.NotRendered,
            RecordingWaitProbeAccounting.Validate(Request(), f.Frame.CompletedRecordingWaitCallback, !render));
    }

    sealed class Tap(string phase, Exception error) : IFrameCaptureTap
    {
        public void Record(FrameCaptureContext context) { if (phase == "capture") throw error; }
        public void AfterSubmit(FrameCaptureContext context) { if (phase == "after-submit") throw error; }
        public void Detach(Action<IDisposable> retire) { }
    }
}
