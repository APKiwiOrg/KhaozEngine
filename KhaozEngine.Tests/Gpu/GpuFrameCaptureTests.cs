using System.Collections.Generic;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>Headless coverage of the Metal frame-capture arm/consume API and the pure present-boundary state
    /// machine that brackets exactly one full frame (all its Submits, between two presents). The Metal interop
    /// itself (MTLCaptureManager) is exercised only on-device.</summary>
    public class GpuFrameCaptureTests
    {
        [Fact]
        public void ArmNext_SetsArmed_AndTryConsumeClearsItOnce()
        {
            GpuFrameCapture.ArmNext("/tmp/x.gputrace");
            Assert.True(GpuFrameCapture.IsArmed);

            Assert.True(GpuFrameCapture.TryConsume(out string p));
            Assert.Equal("/tmp/x.gputrace", p);
            Assert.False(GpuFrameCapture.IsArmed);

            Assert.False(GpuFrameCapture.TryConsume(out _));   // one-shot: already consumed
        }

        // Simulate the device's Present() loop with the pure NextAction helper, recording start/stop boundaries.
        static List<string> RunPresents(int frames, int armBeforePresent)
        {
            var log = new List<string>();
            bool capturing = false;
            for (int present = 0; present < frames; present++)
            {
                if (present == armBeforePresent) GpuFrameCapture.ArmNext("/tmp/cap.gputrace");
                bool consume = !capturing && GpuFrameCapture.TryConsume(out _);
                var action = GpuFrameCapture.NextAction(capturing, consume);
                // (present happens here)
                switch (action)
                {
                    case GpuFrameCapture.CaptureAction.StartAfterPresent: capturing = true; log.Add($"start@{present}"); break;
                    case GpuFrameCapture.CaptureAction.StopAfterPresent: capturing = false; log.Add($"stop@{present}"); break;
                }
            }
            return log;
        }

        [Fact]
        public void Capture_BracketsExactlyOneFullFrame_BetweenTwoPresents()
        {
            // Arm before present index 2. Capture starts at present 2 (so frame 3's submits are recorded) and
            // stops at present 3 (the frame's closing present), bracketing one whole frame and nothing more.
            var log = RunPresents(frames: 6, armBeforePresent: 2);
            Assert.Equal(new[] { "start@2", "stop@3" }, log);
        }

        [Fact]
        public void NextAction_StopTakesPriorityOverANewArmWhileCapturing()
        {
            // While capturing, a present always stops (a new arm is not consumed mid-capture).
            Assert.Equal(GpuFrameCapture.CaptureAction.StopAfterPresent, GpuFrameCapture.NextAction(capturing: true, armConsumed: false));
            Assert.Equal(GpuFrameCapture.CaptureAction.StartAfterPresent, GpuFrameCapture.NextAction(capturing: false, armConsumed: true));
            Assert.Equal(GpuFrameCapture.CaptureAction.None, GpuFrameCapture.NextAction(capturing: false, armConsumed: false));
        }
    }
}
