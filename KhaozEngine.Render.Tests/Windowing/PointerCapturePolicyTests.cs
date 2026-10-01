using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// Pure, headless coverage of the per-frame pointer capture decision. AppWindow applies the returned action to
    /// GLFW and threads <see cref="PointerCaptureAction.AwaitingRenewal"/> into the next frame. No window needed.
    /// </summary>
    public class PointerCapturePolicyTests
    {
        // One frame as AppWindow runs it: decide from the live inputs, then carry the new capture and latch forward.
        sealed class Window
        {
            public bool Captured;
            public bool AwaitingRenewal;

            public PointerCaptureAction Frame(bool requested, bool focused, bool rawMotionSupported = true)
            {
                PointerCaptureAction action = PointerCapturePolicy.Decide(
                    requested, focused, Captured, rawMotionSupported, AwaitingRenewal);
                if (action.SetDisabled) Captured = true;
                if (action.SetNormal) Captured = false;
                AwaitingRenewal = action.AwaitingRenewal;
                return action;
            }
        }

        [Fact]
        public void ARequestWhileFocusedCaptures()
        {
            var window = new Window();

            PointerCaptureAction action = window.Frame(requested: true, focused: true);

            Assert.True(action.SetDisabled);
            Assert.False(action.SetNormal);
            Assert.True(window.Captured);

            var unfocused = new Window();
            PointerCaptureAction refused = unfocused.Frame(requested: true, focused: false);
            Assert.False(refused.SetDisabled);
            Assert.False(unfocused.Captured);
        }

        [Fact]
        public void RawMotionIsSetOnlyWhenSupported()
        {
            PointerCaptureAction unsupported = PointerCapturePolicy.Decide(
                requested: true, focused: true, currentlyCaptured: false, rawMotionSupported: false, awaitingRenewal: false);
            Assert.True(unsupported.SetDisabled);
            Assert.False(unsupported.SetRawMotion);

            PointerCaptureAction supported = PointerCapturePolicy.Decide(
                requested: true, focused: true, currentlyCaptured: false, rawMotionSupported: true, awaitingRenewal: false);
            Assert.True(supported.SetDisabled);
            Assert.True(supported.SetRawMotion);

            // Raw motion rides only the capture call. Releasing and holding never touch it.
            PointerCaptureAction release = PointerCapturePolicy.Decide(
                requested: false, focused: true, currentlyCaptured: true, rawMotionSupported: true, awaitingRenewal: false);
            Assert.False(release.SetRawMotion);
            PointerCaptureAction hold = PointerCapturePolicy.Decide(
                requested: true, focused: true, currentlyCaptured: true, rawMotionSupported: true, awaitingRenewal: false);
            Assert.False(hold.SetRawMotion);
        }

        [Fact]
        public void FocusLossDropsCaptureAndRefocusDoesNotRestoreIt()
        {
            var window = new Window();
            window.Frame(requested: true, focused: true);
            Assert.True(window.Captured);

            // Focus goes on the same edge that releases held buttons, with the request still held.
            PointerCaptureAction lost = window.Frame(requested: true, focused: false);
            Assert.True(lost.SetNormal);
            Assert.False(window.Captured);

            // A renewal while still unfocused does not count either.
            window.Frame(requested: false, focused: false);
            window.Frame(requested: true, focused: false);

            // Refocus with the request still true: the cursor stays free.
            PointerCaptureAction refocused = window.Frame(requested: true, focused: true);
            Assert.False(refocused.SetDisabled);
            Assert.False(window.Captured);
            Assert.False(window.Frame(requested: true, focused: true).SetDisabled);

            // A false then true edge while focused renews the request and captures again.
            Assert.False(window.Frame(requested: false, focused: true).SetDisabled);
            PointerCaptureAction renewed = window.Frame(requested: true, focused: true);
            Assert.True(renewed.SetDisabled);
            Assert.True(window.Captured);
        }

        [Fact]
        public void ReleasingTheRequestRestoresTheNormalCursor()
        {
            var window = new Window();
            window.Frame(requested: true, focused: true);

            PointerCaptureAction action = window.Frame(requested: false, focused: true);

            Assert.True(action.SetNormal);
            Assert.False(action.SetDisabled);
            Assert.False(action.SetRawMotion);
            Assert.False(window.Captured);
        }

        [Theory]
        [InlineData(true, true, true, false)]    // captured and still wanted
        [InlineData(false, true, false, false)]  // free and not wanted
        [InlineData(false, false, false, true)]  // unfocused and not wanted
        [InlineData(true, false, false, true)]   // unfocused with the request held
        [InlineData(true, true, false, true)]    // refocused but the request was never renewed
        public void NoChangeMeansNoGlfwCall(bool requested, bool focused, bool currentlyCaptured, bool awaitingRenewal)
        {
            PointerCaptureAction action = PointerCapturePolicy.Decide(
                requested, focused, currentlyCaptured, rawMotionSupported: true, awaitingRenewal);

            Assert.False(action.SetDisabled);
            Assert.False(action.SetNormal);
            Assert.False(action.SetRawMotion);
            Assert.False(action.CallsGlfw);
        }
    }
}
