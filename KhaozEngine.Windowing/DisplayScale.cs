using System;

namespace KhaozEngine.Windowing
{
    /// <summary>A monitor's size in GLFW window coordinates and how many of those coordinates span one logical
    /// point on it.</summary>
    internal readonly record struct ScreenMetrics(int Width, int Height, float CoordinatesPerPoint);

    /// <summary>A logical window size in points and the exact OS content scale behind it. A zero
    /// <see cref="ContentScale"/> means the OS scale was unavailable and the size is the window-coordinate size.</summary>
    internal readonly record struct LogicalMetrics(int Width, int Height, float ContentScale);

    /// <summary>
    /// Pure display-scale math shared by the frame metrics and the window-sizing cap. The OS content scale is the
    /// UI scaling factor and can differ from the framebuffer-to-window ratio: on Win32 and X11 window coordinates
    /// are pixels, so that ratio is 1 at any OS scale, while Cocoa and Wayland window coordinates are already points.
    /// </summary>
    internal static class DisplayScale
    {
        // glfwGetPlatform results (GLFW 3.4).
        internal const int GlfwPlatformWin32 = 0x00060001;
        internal const int GlfwPlatformCocoa = 0x00060002;
        internal const int GlfwPlatformWayland = 0x00060003;
        internal const int GlfwPlatformX11 = 0x00060004;

        internal static bool IsUsable(float scale) => float.IsFinite(scale) && scale > 0f;

        /// <summary>Window coordinates per logical point. Only platforms whose coordinates are pixels convert, and an
        /// unknown platform or unusable scale reports 1 so callers keep their coordinate-unit behavior.</summary>
        internal static float CoordinatesPerPoint(int glfwPlatform, float contentScale)
            => (glfwPlatform == GlfwPlatformWin32 || glfwPlatform == GlfwPlatformX11) && IsUsable(contentScale)
                ? contentScale
                : 1f;

        /// <summary>The logical size for a framebuffer at the exact OS content scale. An unusable scale keeps the
        /// window-coordinate size, which leaves the scale to the framebuffer-to-window ratio as before.</summary>
        internal static LogicalMetrics Logical(int framebufferWidth, int framebufferHeight, int windowWidth,
            int windowHeight, float contentScale)
            => IsUsable(contentScale)
                ? new LogicalMetrics(Points(framebufferWidth, contentScale), Points(framebufferHeight, contentScale),
                    contentScale)
                : new LogicalMetrics(windowWidth, windowHeight, 0f);

        static int Points(int pixels, float scale) => pixels > 0 ? Math.Max(1, (int)MathF.Round(pixels / scale)) : 0;
    }
}
