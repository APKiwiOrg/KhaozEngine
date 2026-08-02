using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// An immutable snapshot of the runtime-mutable display state: how the window presents
    /// (<see cref="PresentMode"/>), the optional software frame cap (<see cref="FrameCapHz"/>), how the window
    /// occupies the display (<see cref="WindowMode"/>), the windowed size in logical points
    /// (<see cref="Width"/> x <see cref="Height"/>), and the window top-left position in virtual-desktop
    /// coordinates (<see cref="X"/>, <see cref="Y"/>, which monitor is implied by the absolute position). Read one
    /// from <see cref="IDisplaySettings.CurrentDisplay"/>, tweak fields, and hand it back to
    /// <see cref="IDisplaySettings.ApplyDisplay"/> - a `with`-friendly value type so a settings menu can round-trip
    /// the whole placement (mode + present + cap + size + position) in one call. <see cref="X"/> / <see cref="Y"/>
    /// default to <see cref="PositionUnspecified"/> so a hand-built snapshot without a position leaves the window
    /// where it is.
    /// </summary>
    public readonly record struct DisplaySettings(
        PresentMode PresentMode,
        int FrameCapHz,
        WindowMode WindowMode,
        int Width,
        int Height,
        int X = int.MinValue,
        int Y = int.MinValue)
    {
        /// <summary>Sentinel for <see cref="X"/> / <see cref="Y"/> meaning "position unspecified" (leave the window
        /// where it is). <see cref="IDisplaySettings.CurrentDisplay"/> always fills real coordinates.</summary>
        public const int PositionUnspecified = int.MinValue;

        /// <summary>True when both <see cref="X"/> and <see cref="Y"/> carry a real position (not the
        /// <see cref="PositionUnspecified"/> sentinel), so <see cref="IDisplaySettings.ApplyDisplay"/> should place
        /// the window (after clamping it on-screen).</summary>
        public bool HasPosition => X != PositionUnspecified && Y != PositionUnspecified;

        /// <summary>
        /// True when <paramref name="presentMode"/> cannot deterministically cap the frame rate on
        /// <paramref name="backend"/> without a software <see cref="FrameCapHz"/>: vsync selected, no frame cap, on
        /// Metal - where the Veldrid Metal present does not throttle the CPU from vsync alone (a Mac client free-runs
        /// well above the refresh). Pure and headless-testable; <see cref="AppWindow"/> uses it to emit a one-time
        /// warning so a consumer knows to set <see cref="FrameCapHz"/> for a real cap on macOS.
        /// <para>
        /// Equality against Metal, not a family predicate, and deliberately so: this is the same arm as
        /// <see cref="FrameCap.Resolve"/> and it takes the same decision. An appended backend warns about nothing,
        /// which is correct for <see cref="GpuBackendKind.Direct3D11Native"/>, whose vsync throttles the CPU
        /// exactly as the incumbent Direct3D11 path's does.
        /// </para>
        /// </summary>
        public static bool RequiresFrameCapWarning(GpuBackendKind backend, PresentMode presentMode, int frameCapHz)
            => backend == GpuBackendKind.Metal && presentMode == PresentMode.Vsync && frameCapHz <= 0;
    }

    /// <summary>
    /// The cohesive runtime display-control surface: present mode, frame cap, window mode, resolution, and window
    /// placement (position + monitor), all settable mid-session with no crash and no leaked swapchain. Implemented
    /// by <see cref="AppWindow"/> and surfaced on the <c>GameApp</c> facade (<c>GameApp.Display</c>). Each member is
    /// safe to call at any time after the window exists; <see cref="ApplyDisplay"/> applies a whole
    /// <see cref="DisplaySettings"/> at once (window mode and resolution first, then placement, then frame cap, then
    /// present mode) for a settings-screen "Apply" button. Position + monitor let a consumer persist and restore the
    /// full window placement across launches; <see cref="EnsureVisible"/> clamps a restored window back on-screen.
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

        /// <summary>Current window top-left X in virtual-desktop (screen) coordinates.</summary>
        int WindowX { get; }

        /// <summary>Current window top-left Y in virtual-desktop (screen) coordinates.</summary>
        int WindowY { get; }

        /// <summary>Move the window top-left to (<paramref name="x"/>, <paramref name="y"/>) in virtual-desktop
        /// coordinates. Applied immediately in <see cref="WindowMode.Windowed"/>; in a fullscreen mode it is
        /// remembered as the windowed position to restore. Symmetric with <see cref="Resize"/>.</summary>
        void MoveTo(int x, int y);

        /// <summary>The connected monitors (index, name, bounds in window coordinates). Empty when no display is
        /// available (headless).</summary>
        IReadOnlyList<MonitorInfo> Monitors { get; }

        /// <summary>Index into <see cref="Monitors"/> of the monitor currently holding the window (the one containing
        /// its centre, else greatest overlap / nearest), or -1 when unknown (headless).</summary>
        int CurrentMonitorIndex { get; }

        /// <summary>Place the window on the monitor at <paramref name="index"/> into <see cref="Monitors"/>: centred
        /// when windowed, covering the monitor when borderless fullscreen. Out-of-range indices are ignored.</summary>
        void MoveToMonitor(int index);

        /// <summary>Clamp the window back on-screen, e.g. after restoring a saved position whose monitor is gone
        /// (unplugged / different layout). A no-op when the window is already adequately visible.</summary>
        void EnsureVisible();

        /// <summary>A snapshot of the current display state, including position (safe to read any time).</summary>
        DisplaySettings CurrentDisplay { get; }

        /// <summary>Apply a whole settings snapshot mid-session. Order: window mode, then resolution, then placement
        /// (clamp on-screen + move, when the snapshot carries a position), then frame cap, then present mode (so the
        /// Metal vsync/cap warning reflects the final frame cap). Safe to call at any time; no swapchain is recreated
        /// for a present-mode change and none is leaked for a resolution change.</summary>
        void ApplyDisplay(in DisplaySettings settings);
    }
}
