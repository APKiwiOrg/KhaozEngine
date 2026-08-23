namespace KhaozEngine.Windowing
{
    /// <summary>
    /// How the window presents finished frames. Pair with a frame cap (<see cref="AppWindow.FrameCapHz"/> /
    /// <c>GameAppOptions.FrameCapHz</c>) to pin the render rate to an integer multiple of a fixed simulation/network
    /// tick, which keeps presentation phase-aligned with the tick and removes any residual beat.
    /// </summary>
    public enum PresentMode
    {
        /// <summary>Sync presentation to the display's vertical blank (the swapchain's <c>SyncToVerticalBlank</c>).
        /// Caps to the refresh rate where the backend honours it. NOTE: the Veldrid Metal path did not reliably
        /// throttle the CPU-side frame rate from this alone (a Mac client could free-run well above the refresh),
        /// and <see cref="FrameCap.Auto"/> resolved to a real cap there. That backend was deleted in 18.0.0 and the
        /// native Metal present throttles from vsync, so no explicit
        /// <see cref="AppWindow.FrameCapHz"/> is needed.</summary>
        Vsync,

        /// <summary>Present with no vertical-blank sync (lowest latency, tearing possible, uncapped fps unless a
        /// <see cref="AppWindow.FrameCapHz"/> is set).</summary>
        Immediate,
    }
}
