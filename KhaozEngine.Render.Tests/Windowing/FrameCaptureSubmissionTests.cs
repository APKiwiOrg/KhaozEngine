using System;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class FrameCaptureSubmissionTests
{
    [Fact]
    public void Captured_frame_submits_coverage_and_tap_fences_once_with_independent_lifetimes()
    {
        using var device = new OpenListTrackingGpuDevice(completionFences: true) { CompleteSubmissions = true };
        using var commands = device.Factory.CreateCommandList();
        using var caller = device.Factory.CreateFence();
        using var taps = new FrameCaptureTaps(device, null);
        var frame = new Frame { Commands = commands };
        var consumer = new Consumer();
        IGpuFence? slot = null;
        taps.Add(new Tap(c => c.SignalOnSubmit(caller), _ =>
        {
            Assert.Equal(1, consumer.Submissions);
            Assert.True(caller.Signaled);
            caller.Reset();
            Assert.True(slot!.Signaled);
            device.Order.Add("after");
        }));
        try
        {
            FramePhases.Run(frame, true, device, commands, Color.Black, f =>
            {
                Assert.True(f.Submission.TryRegister(consumer));
                Assert.True(f.Submission.TryRetain(consumer, out slot));
            }, _ => { }, taps, 16);

            Assert.Equal(1, device.Submits);
            Assert.Equal(1, device.FencedSubmits);
            Assert.Equal(2, device.SubmittedFenceCount);
            Assert.Same(slot, device.FirstSubmittedFence);
            Assert.Same(caller, device.SecondSubmittedFence);
            Assert.Equal(new[] { "submit+fence", "after", "present" }, device.Order);
            Assert.Equal(0, consumer.Abandons);
        }
        finally { frame.DisposeSubmission(); }
    }

    [Theory]
    [InlineData("prepare", false)]
    [InlineData("begin", false)]
    [InlineData("frame", false)]
    [InlineData("tap", false)]
    [InlineData("prepare", true)]
    [InlineData("begin", true)]
    [InlineData("frame", true)]
    [InlineData("tap", true)]
    public void Captured_frame_failure_before_submit_abandons_the_begun_submission(string phase, bool rendererOwned)
    {
        using var device = new OpenListTrackingGpuDevice(completionFences: true);
        using var commands = device.Factory.CreateCommandList();
        using var taps = new FrameCaptureTaps(device, null);
        var frame = new Frame { Commands = commands };
        var consumer = new Consumer();
        int after = 0;
        taps.Add(new Tap(_ => Fail("tap"), _ => after++));
        if (phase == "begin") device.BeforeBegin = () => Fail("begin");
        try
        {
            var failure = Assert.Throws<InvalidOperationException>(() =>
                RendererFramePolicy.Run(frame, true, device, commands, Color.Black, f =>
                {
                    Assert.True(f.Submission.TryRegister(consumer));
                    Fail("prepare");
                }, _ => Fail("frame"), taps, rendererOwned));
            Assert.Equal("planned " + phase, failure.Message);
            Assert.Equal(1, consumer.Abandons);
            Assert.Equal(0, consumer.Submissions);
            Assert.Equal(0, device.Submits);
            Assert.Equal(0, device.Presents);
            Assert.Equal(0, device.OpenLists);
            Assert.Equal(0, after);
        }
        finally { frame.DisposeSubmission(); }

        void Fail(string at) { if (phase == at) throw new InvalidOperationException("planned " + at); }
    }

    [Fact]
    public void Suppressed_captured_frame_abandons_without_running_a_tap()
    {
        using var device = new OpenListTrackingGpuDevice(completionFences: true);
        using var commands = device.Factory.CreateCommandList();
        using var taps = new FrameCaptureTaps(device, null);
        var frame = new Frame { Commands = commands, RenderSuppressed = true };
        var consumer = new Consumer();
        taps.Add(new Tap(_ => throw new InvalidOperationException("unexpected tap")));
        try
        {
            FramePhases.Run(frame, false, device, commands, Color.Black,
                f => Assert.True(f.Submission.TryRegister(consumer)), _ => { }, taps, 16);
            Assert.Equal(1, consumer.Abandons);
            Assert.Equal(0, device.Submits);
            Assert.Equal(0, device.Presents);
        }
        finally { frame.DisposeSubmission(); }
    }

    [Fact]
    public void Throwing_after_submit_keeps_the_submitted_notification()
    {
        using var device = new OpenListTrackingGpuDevice(completionFences: true);
        using var commands = device.Factory.CreateCommandList();
        using var taps = new FrameCaptureTaps(device, null);
        var frame = new Frame { Commands = commands };
        var consumer = new Consumer();
        taps.Add(new Tap(_ => { }, _ => throw new InvalidOperationException("after submit")));
        try
        {
            var failure = Assert.Throws<InvalidOperationException>(() => FramePhases.Run(frame, true, device,
                commands, Color.Black, f => Assert.True(f.Submission.TryRegister(consumer)), _ => { }, taps, 16));
            Assert.Equal("after submit", failure.Message);
            Assert.Equal(1, device.Submits);
            Assert.Equal(1, consumer.Submissions);
            Assert.Equal(0, consumer.Abandons);
            Assert.Equal(0, device.Presents);
        }
        finally { frame.DisposeSubmission(); }
    }

    sealed class Tap(Action<FrameCaptureContext> record, Action<FrameCaptureContext>? after = null) : IFrameCaptureTap
    {
        public void Record(FrameCaptureContext context) => record(context);
        public void AfterSubmit(FrameCaptureContext context) => after?.Invoke(context);
        public void Detach(Action<IDisposable> retire) { }
    }

    sealed class Consumer : IGpuFrameCompletionConsumer, IGpuFrameSubmissionParticipant
    {
        internal int Submissions, Abandons;
        public bool HasPending(IGpuFence completion) => false;
        public void DiscardUnsubmitted(IGpuFence completion) { }
        public void ReleaseCompleted(IGpuFence completion) { }
        public void Submitted() => Submissions++;
        public void Abandoned() => Abandons++;
    }
}
