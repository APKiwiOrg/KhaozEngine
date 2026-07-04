namespace KhaozEngine.Windowing
{
    /// <summary>
    /// How the window occupies the display. Settable at runtime via <see cref="AppWindow.WindowMode"/> /
    /// <see cref="IDisplaySettings"/>; the swapchain follows the resulting framebuffer size automatically.
    /// </summary>
    public enum WindowMode
    {
        /// <summary>A normal, resizable, decorated window at its logical size (the default).</summary>
        Windowed,

        /// <summary>A borderless window sized to cover the current monitor at the desktop resolution (no video-mode
        /// switch). The friendly "windowed fullscreen": instant alt-tab, no mode flicker.</summary>
        BorderlessFullscreen,

        /// <summary>True exclusive fullscreen (the OS/driver gives the window the display and may switch the video
        /// mode). Lowest present latency; alt-tab is heavier. On macOS/Metal this maps to native fullscreen.</summary>
        ExclusiveFullscreen,
    }
}
