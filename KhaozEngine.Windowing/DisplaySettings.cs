using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// An immutable snapshot of the runtime-mutable display state: how the window presents
    /// (<see cref="PresentMode"/>), the optional software frame cap (<see cref="FrameCapHz"/>), how the window
    /// occupies the display (<see cref="WindowMode"/>), and the windowed size in logical points
    /// (<see cref="Width"/> x <see cref="Height"/>). Read one from <see cref="IDisplaySettings.CurrentDisplay"/>,
    /// tweak fields, and hand it back to <see cref="IDisplaySettings.ApplyDisplay"/> - a `with`-friendly value type
    /// so a settings menu can round-trip the whole surface in one call.
    /// </summary>
    public readonly record struct DisplaySettings(
        PresentMode PresentMode,
        int FrameCapHz,
        WindowMode WindowMode,
        int Width,
        int Height)
    {
        /// <summary>
        /// True when <paramref name="presentMode"/> cannot deterministically cap the frame rate on
        /// <paramref name="backend"/> without a software <see cref="FrameCapHz"/>: vsync selected, no frame cap, on
        /// Metal - where the Veldrid Metal present does not throttle the CPU from vsync alone (a Mac client free-runs
        /// well above the refresh). Pure and headless-testable; <see cref="AppWindow"/> uses it to emit a one-time
        /// warning so a consumer knows to set <see cref="FrameCapHz"/> for a real cap on macOS.
        /// </summary>
        public static bool RequiresFrameCapWarning(GpuBackendKind backend, PresentMode presentMode, int frameCapHz)
            => backend == GpuBackendKind.Metal && presentMode == PresentMode.Vsync && frameCapHz <= 0;
    }

    /// <summary>
    /// The cohesive runtime display-control surface: present mode, frame cap, window mode, and resolution, all
    /// settable mid-session with no crash and no leaked swapchain. Implemented by <see cref="AppWindow"/> and
    /// surfaced on the <c>GameApp</c> facade (<c>GameApp.Display</c>). Each member is safe to call at any time after
    /// the window exists; <see cref="ApplyDisplay"/> applies a whole <see cref="DisplaySettings"/> at once (window
    /// mode and resolution first, then frame cap, then present mode) for a settings-screen "Apply" button.
    /// </summary>
    public interface IDisplaySettings
    {
        /// <summary>How the window presents frames. Setting it reconfigures the live swapchain's vsync in place
        /// (no recreate). On Metal, pair vsync with <see cref="FrameCapHz"/> for a deterministic cap.</summary>
        PresentMode PresentMode { get; set; }

        /// <summary>Software frame-rate cap in Hz (0 = uncapped). Takes effect next frame.</summary>
        int FrameCapHz { get; set; }

        /// <summary>How the window occupies the display (windowed / borderless / exclusive fullscreen). The swapchain
        /// follows the resulting framebuffer size automatically.</summary>
        WindowMode WindowMode { get; set; }

        /// <summary>Current logical window width in points (the windowed size; the design/render size derives from the
        /// framebuffer, which is this scaled by the HiDPI factor).</summary>
        int WindowWidth { get; }

        /// <summary>Current logical window height in points.</summary>
        int WindowHeight { get; }

        /// <summary>Set the windowed size in logical points. Applied immediately in <see cref="WindowMode.Windowed"/>
        /// (the swapchain follows); in a fullscreen mode it is remembered as the size to restore when returning to
        /// windowed. Non-positive sizes are ignored.</summary>
        void Resize(int width, int height);

        /// <summary>A snapshot of the current display state (safe to read any time).</summary>
        DisplaySettings CurrentDisplay { get; }

        /// <summary>Apply a whole settings snapshot mid-session. Order: window mode, then resolution, then frame cap,
        /// then present mode (so the Metal vsync/cap warning reflects the final frame cap). Safe to call at any time;
        /// no swapchain is recreated for a present-mode change and none is leaked for a resolution change.</summary>
        void ApplyDisplay(in DisplaySettings settings);
    }
}
