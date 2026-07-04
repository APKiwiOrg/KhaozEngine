namespace KhaozEngine.Windowing
{
    /// <summary>Engine-neutral window state target (the subset the planner drives): a normal window or fullscreen.</summary>
    public enum WindowStateTarget { Normal, Fullscreen }

    /// <summary>Engine-neutral window border target: a normal resizable border, or none (borderless).</summary>
    public enum WindowBorderTarget { Resizable, Hidden }

    /// <summary>
    /// The concrete window state to apply for a <see cref="WindowMode"/> (produced by
    /// <see cref="WindowModePlanner.Compute"/>). Silk-free so it is unit-testable without a live window;
    /// <see cref="AppWindow"/> maps it onto the Silk window. <see cref="SetPosition"/> / <see cref="SetSize"/> gate
    /// the geometry writes so exclusive fullscreen (which the OS positions/sizes itself) leaves them untouched.
    /// </summary>
    public readonly record struct WindowModePlan(
        WindowStateTarget State,
        WindowBorderTarget Border,
        bool SetPosition, int X, int Y,
        bool SetSize, int Width, int Height);

    /// <summary>
    /// Pure policy mapping a <see cref="WindowMode"/> to the window state / border / geometry to apply. No monitor
    /// or GPU access, so it is fully headless-testable (mirrors the <see cref="AppWindow.FitToScreen"/> style).
    /// </summary>
    public static class WindowModePlanner
    {
        /// <summary>
        /// Compute the window state to realise <paramref name="mode"/> on a monitor whose bounds are
        /// (<paramref name="monitorX"/>, <paramref name="monitorY"/>, <paramref name="monitorWidth"/> x
        /// <paramref name="monitorHeight"/>) in window coordinates, restoring
        /// <paramref name="windowedWidth"/> x <paramref name="windowedHeight"/> when returning to
        /// <see cref="WindowMode.Windowed"/>.
        /// <list type="bullet">
        /// <item><see cref="WindowMode.Windowed"/>: normal state + resizable border, sized to the windowed size.
        /// Moved to (<paramref name="windowedX"/>, <paramref name="windowedY"/>) when
        /// <paramref name="restoreWindowedPos"/> is set (a remembered position to restore), otherwise position is
        /// left untouched and the OS keeps it where it is.</item>
        /// <item><see cref="WindowMode.BorderlessFullscreen"/>: normal state + hidden border, moved to the monitor
        /// origin and sized to the monitor. If the monitor size is unknown (&lt;= 0, e.g. headless), it falls back to
        /// the windowed size and skips the reposition so it never yields a zero-size window.</item>
        /// <item><see cref="WindowMode.ExclusiveFullscreen"/>: fullscreen state; geometry left to the OS/driver.</item>
        /// </list>
        /// </summary>
        public static WindowModePlan Compute(WindowMode mode,
            int monitorX, int monitorY, int monitorWidth, int monitorHeight,
            int windowedWidth, int windowedHeight,
            bool restoreWindowedPos = false, int windowedX = 0, int windowedY = 0)
        {
            bool haveMonitor = monitorWidth > 0 && monitorHeight > 0;
            return mode switch
            {
                WindowMode.ExclusiveFullscreen => new WindowModePlan(
                    WindowStateTarget.Fullscreen, WindowBorderTarget.Hidden,
                    SetPosition: false, 0, 0, SetSize: false, 0, 0),

                WindowMode.BorderlessFullscreen when haveMonitor => new WindowModePlan(
                    WindowStateTarget.Normal, WindowBorderTarget.Hidden,
                    SetPosition: true, monitorX, monitorY, SetSize: true, monitorWidth, monitorHeight),

                WindowMode.BorderlessFullscreen => new WindowModePlan(
                    WindowStateTarget.Normal, WindowBorderTarget.Hidden,
                    SetPosition: false, 0, 0, SetSize: true, windowedWidth, windowedHeight),

                _ => new WindowModePlan(
                    WindowStateTarget.Normal, WindowBorderTarget.Resizable,
                    SetPosition: restoreWindowedPos, windowedX, windowedY, SetSize: true, windowedWidth, windowedHeight),
            };
        }
    }
}
