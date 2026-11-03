using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class FrameTimingSplitTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Released_split_is_observed_without_controlling_it_and_foreign_lists_are_excluded(bool sampling)
    {
        using var harness = new MetalRingHarness(framesInFlight: 3);
        using var frame = harness.NewList(new());
        using var foreign = harness.NewList(new());
        frame.EarlySubmission = new(new ObservedPrefixApi(), harness.SubmitLock);
        using var capture = new FrameTimingCapture(frame, 16);
        if (sampling) capture.Start();
        Record(0);
        // Callback 1 renders nothing. The next callback still gets its own prefix and final identities.
        Record(2);
        MetalFrameTimingProbe.Submit(new(900), new(8), FrameTimingCommandKind.EarlyFrameRecording, foreign);
        MetalFrameTimingProbe.PrefixCommitted(foreign);
        capture.Stop();
        var counts = capture.EarlySubmit(3);
        Assert.Equal(sampling ? new[] { 1, 0, 1 } : new[] { 0, 0, 0 }, counts.PrefixCommitsPerCallback);
        Assert.Equal(sampling ? new[] { true, false, true } : new[] { false, false, false }, counts.AppliedPerCallback);
        Assert.Equal(sampling ? 2 : 0, counts.AppliedCallbacks);
        var submits = capture.Snapshot().Submissions.Records;
        if (!sampling) Assert.Empty(submits);
        else
        {
            Assert.Equal(new[] { FrameTimingCommandKind.EarlyFrameRecording, FrameTimingCommandKind.FrameRecording,
                FrameTimingCommandKind.EarlyFrameRecording, FrameTimingCommandKind.FrameRecording, FrameTimingCommandKind.Other }, submits.Select(s => s.Kind));
            Assert.Equal(new[] { 0, 0, 2, 2, 2 }, submits.Select(s => s.CallbackIndex));
            Assert.NotEqual(submits[0].CommandBuffer, submits[1].CommandBuffer);
        }

        void Record(int callback)
        {
            capture.CallbackIndex = callback;
            frame.Begin();
            MetalSwapchainFramebuffer? drawable = null;
            drawable = new(GpuPixelFormat.R8G8B8A8UNorm, new(16, 16),
                () => drawable!.Adopt(new(new(0x777), GpuPixelFormat.R8G8B8A8UNorm), new(16, 16)));
            frame.SetFramebuffer(drawable);
            frame.End();
            MetalFrameTimingProbe.Submit(frame.SealedCommandBuffer, new(8), FrameTimingCommandKind.Other, frame);
            ulong final = harness.Submit(frame);
            harness.Event.Completed = final;
        }
    }

    sealed class ObservedPrefixApi : IMetalEarlyFrameApi
    {
        public bool IsAlive => true;
        public void FlushSetup() { }
        public void CommitWithCompletionHandler(IntPtr buffer, MetalCommandList list)
            => MetalFrameTimingProbe.Submit(buffer, new(8), FrameTimingCommandKind.EarlyFrameRecording, list);
    }
}
