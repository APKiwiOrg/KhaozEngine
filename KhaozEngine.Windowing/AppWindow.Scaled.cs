using System;
using KhaozEngine.Gpu;
using Silk.NET.Windowing.Glfw;

namespace KhaozEngine.Windowing;

public sealed partial class AppWindow
{
    /// <summary>
    /// Open a display-fitted window with an explicit launch-focus policy. Sizing follows
    /// <see cref="FitToScreen"/>. A false <paramref name="focusOnLaunch"/> requests no keyboard focus,
    /// subject to <c>KE_WINDOW_FOCUS</c> and the constructor's platform caveats.
    /// <para>The required focus argument comes first so existing calls to the original factory remain
    /// unambiguous. All other defaults are unchanged. Positive <paramref name="frameCapHz"/> values
    /// request that cap, and non-positive values request <see cref="Windowing.FrameCap.Uncapped"/>.</para>
    /// </summary>
    public static AppWindow Scaled(bool focusOnLaunch, string title, int designWidth, int designHeight,
        float screenFraction = 0.9f, float maxScale = 2f,
        PresentMode presentMode = PresentMode.Vsync, int frameCapHz = 0,
        GpuBackendKind? backendPreference = null)
    {
        GlfwWindowing.RegisterPlatform();
        return CreateScaled(PrimaryScreenMetrics,
            static (title, width, height, present, cap, backend, focus)
                => new AppWindow(title, width, height, present, cap, backend, focus),
            title, designWidth, designHeight, screenFraction, maxScale,
            presentMode, frameCapHz, backendPreference, focusOnLaunch);
    }

    // Shared factory execution. Native monitor access and window construction are supplied at the boundary,
    // so the fitted size and creation arguments can be exercised without a display or device.
    internal static T CreateScaled<T>(Func<ScreenMetrics> screen,
        Func<string, int, int, PresentMode, FrameCap, GpuBackendKind?, bool, T> create,
        string title, int designWidth, int designHeight,
        float screenFraction = 0.9f, float maxScale = 2f,
        PresentMode presentMode = PresentMode.Vsync, int frameCapHz = 0,
        GpuBackendKind? backendPreference = null, bool focusOnLaunch = true)
    {
        // maxScale counts logical points. FitToScreen works in window coordinates, so convert the cap.
        var metrics = screen();
        float coordinatesPerPoint = DisplayScale.IsUsable(metrics.CoordinatesPerPoint)
            ? metrics.CoordinatesPerPoint
            : 1f;
        var (w, h) = FitToScreen(designWidth, designHeight, metrics.Width, metrics.Height, screenFraction,
            maxScale * coordinatesPerPoint);
        return create(title, w, h, presentMode,
            frameCapHz > 0 ? FrameCap.Hz(frameCapHz) : FrameCap.Uncapped,
            backendPreference, focusOnLaunch);
    }
}
