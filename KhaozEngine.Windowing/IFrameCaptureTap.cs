using System;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// A per-frame hook into the window's frame, for work that reads the frame the game just rendered: a
    /// screenshot, a rolling clip. Register one with <see cref="AppWindow.AddCaptureTap"/>. Every callback runs on
    /// the frame thread, and only on a frame that renders. A render-suppressed (minimised) frame runs none.
    /// </summary>
    public interface IFrameCaptureTap
    {
        /// <summary>
        /// Runs inside the frame's recording, after the game's draws and before the frame's list is submitted.
        /// Record copies into <see cref="FrameCaptureContext.Commands"/> here. Never open another command list.
        /// </summary>
        void Record(FrameCaptureContext context);

        /// <summary>
        /// Runs after the frame's submit and before its present, with nothing recording, so a map or a blocking
        /// readback is legal here.
        /// </summary>
        void AfterSubmit(FrameCaptureContext context);

        /// <summary>
        /// Runs once, when the tap is removed or the window is disposed. Hand every GPU resource the tap owns to
        /// <paramref name="retire"/>, which destroys it once the GPU has finished with it. No callback follows.
        /// </summary>
        void Detach(Action<IDisposable> retire);
    }
}
