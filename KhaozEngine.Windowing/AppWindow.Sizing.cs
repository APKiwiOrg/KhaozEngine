using System;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// Window-SIZING policy on <see cref="AppWindow"/>: the display-fitted <see cref="AppWindow.Scaled"/> factory
    /// and the two helpers behind it. They are here rather than in <c>AppWindow.cs</c> because that file is at its
    /// size ceiling, and because sizing is a distinct concern from the frame loop: nothing here pumps input,
    /// renders, or presents, and <see cref="AppWindow.FitToScreen"/> is pure enough to unit-test with no window.
    /// </summary>
    public sealed partial class AppWindow
    {
        /// <summary>
        /// Open a window for a fixed design resolution, sized up to fill the display. The window opens at the
        /// largest multiple of (<paramref name="designWidth"/> x <paramref name="designHeight"/>) that preserves the
        /// design aspect and fits within <paramref name="screenFraction"/> of the primary monitor's work area,
        /// clamped to [1, <paramref name="maxScale"/>]. A small-tall (portrait) design on a desktop monitor thus
        /// opens large enough to read instead of at life-size; pair with a <c>DesignViewport</c> (Fit) so the whole
        /// UI scales uniformly. Never opens smaller than the design size, and falls back to it if the monitor size
        /// is unavailable.
        /// <para><paramref name="backendPreference"/> is forwarded to the constructor: the player's stored
        /// graphics-backend choice, outranking the OS probe and outranked by <c>KE_GRAPHICS_BACKEND</c>.</para>
        /// </summary>
        public static AppWindow Scaled(string title, int designWidth, int designHeight,
            float screenFraction = 0.9f, float maxScale = 2f,
            PresentMode presentMode = PresentMode.Vsync, int frameCapHz = 0,
            GpuBackendKind? backendPreference = null)
        {
            GlfwWindowing.RegisterPlatform();
            var (sw, sh) = PrimaryScreenSize();
            var (w, h) = FitToScreen(designWidth, designHeight, sw, sh, screenFraction, maxScale);
            return new AppWindow(title, w, h, presentMode,
                frameCapHz > 0 ? FrameCap.Hz(frameCapHz) : FrameCap.Uncapped, backendPreference);
        }

        /// <summary>
        /// Pure window-sizing policy (no monitor / GPU access, so it is unit-testable): the largest size with the
        /// design's aspect ratio that fits within <paramref name="screenFraction"/> of a
        /// <paramref name="screenWidth"/> x <paramref name="screenHeight"/> display, expressed as a uniform scale of
        /// the design clamped to [1, <paramref name="maxScale"/>]. Returns the design size unchanged when the screen
        /// size is unknown (&lt;= 0) or too small to grow into.
        /// </summary>
        public static (int Width, int Height) FitToScreen(int designWidth, int designHeight,
            int screenWidth, int screenHeight, float screenFraction = 0.9f, float maxScale = 2f)
        {
            if (designWidth <= 0 || designHeight <= 0) return (designWidth, designHeight);
            if (screenWidth <= 0 || screenHeight <= 0) return (designWidth, designHeight);

            float availW = screenWidth * screenFraction;
            float availH = screenHeight * screenFraction;
            // Uniform scale that keeps the design fully inside the available area on both axes.
            float scale = ViewportMath.Fit(designWidth, designHeight, availW, availH);
            scale = Math.Clamp(scale, 1f, MathF.Max(1f, maxScale));
            return ((int)MathF.Round(designWidth * scale), (int)MathF.Round(designHeight * scale));
        }

        /// <summary>
        /// The primary monitor's size in window coordinates, or (0, 0) if it cannot be determined. Requires the
        /// Silk GLFW platform to be registered (the constructors / <see cref="Scaled"/> do this).
        /// </summary>
        public static (int Width, int Height) PrimaryScreenSize()
        {
            try
            {
                IMonitor? monitor = Monitor.GetMainMonitor(null);
                if (monitor == null) return (0, 0);
                Vector2D<int> size = monitor.Bounds.Size;
                return (size.X, size.Y);
            }
            catch
            {
                return (0, 0); // headless / no display: caller falls back to the design size.
            }
        }
    }
}
