using System;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class AppWindowScaledTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_focus_reaches_creation_with_fitted_size_and_display_settings(bool focus)
    {
        var created = AppWindow.CreateScaled(() => Screen(1920, 1080), Capture, "scaled", 640, 360,
            screenFraction: 0.75f, maxScale: 4f, presentMode: PresentMode.Immediate,
            frameCapHz: 120, backendPreference: GpuBackendKind.VulkanNative, focusOnLaunch: focus);

        Assert.Equal("scaled", created.Title);
        Assert.Equal(1440, created.Width);
        Assert.Equal(810, created.Height);
        Assert.Equal(PresentMode.Immediate, created.PresentMode);
        Assert.Equal(FrameCap.Hz(120), created.FrameCap);
        Assert.Equal(GpuBackendKind.VulkanNative, created.Backend);
        Assert.Equal(new WindowCreationHints(focus, focus), created.Hints);
    }

    [Fact]
    public void Legacy_defaults_keep_focus_vsync_uncapped_and_the_default_scale_limit()
    {
        var created = AppWindow.CreateScaled(() => Screen(3840, 2160), Capture, "legacy", 640, 360);

        Assert.Equal(1280, created.Width);
        Assert.Equal(720, created.Height);
        Assert.Equal(PresentMode.Vsync, created.PresentMode);
        Assert.Equal(FrameCap.Uncapped, created.FrameCap);
        Assert.Null(created.Backend);
        Assert.Equal(new WindowCreationHints(true, true), created.Hints);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Non_positive_caps_stay_explicitly_uncapped_and_unknown_screens_keep_design_size(int cap)
    {
        var created = AppWindow.CreateScaled(() => Screen(0, 0), Capture, "headless", 800, 600,
            frameCapHz: cap, backendPreference: GpuBackendKind.MetalNative, focusOnLaunch: false);

        Assert.Equal(800, created.Width);
        Assert.Equal(600, created.Height);
        Assert.Equal(FrameCap.Uncapped, created.FrameCap);
        Assert.Equal(GpuBackendKind.MetalNative, created.Backend);
        Assert.Equal(new WindowCreationHints(false, false), created.Hints);
    }

    [Fact]
    public void Default_screen_fraction_and_small_screens_preserve_existing_sizing_policy()
    {
        var grown = AppWindow.CreateScaled(() => Screen(1920, 1080), Capture, "grown", 640, 360, maxScale: 4f);
        Assert.Equal(1728, grown.Width);
        Assert.Equal(972, grown.Height);

        var small = AppWindow.CreateScaled(() => Screen(320, 240), Capture, "small", 640, 360);
        Assert.Equal(640, small.Width);
        Assert.Equal(360, small.Height);
    }

    [Fact]
    public void Legacy_binary_signature_defaults_and_source_call_shapes_remain_available()
    {
        var legacy = typeof(AppWindow).GetMethod(nameof(AppWindow.Scaled), new[]
        {
            typeof(string), typeof(int), typeof(int), typeof(float), typeof(float),
            typeof(PresentMode), typeof(int), typeof(GpuBackendKind?)
        });
        Assert.NotNull(legacy);
        Assert.Equal(typeof(AppWindow), legacy.ReturnType);
        var parameters = legacy.GetParameters();
        Assert.Equal(0.9f, parameters[3].DefaultValue);
        Assert.Equal(2f, parameters[4].DefaultValue);
        Assert.Equal((int)PresentMode.Vsync, Convert.ToInt32(parameters[5].DefaultValue));
        Assert.Equal(0, parameters[6].DefaultValue);
        Assert.Null(parameters[7].DefaultValue);

        Func<string, int, int, float, float, PresentMode, int, GpuBackendKind?, AppWindow> oldFactory = AppWindow.Scaled;
        Func<bool, string, int, int, float, float, PresentMode, int, GpuBackendKind?, AppWindow> focusFactory = AppWindow.Scaled;
        Func<AppWindow> defaultLiteral = () => AppWindow.Scaled("legacy", 640, 360, default);
        Func<AppWindow> namedFocus = () => AppWindow.Scaled(title: "new", designWidth: 640,
            designHeight: 360, focusOnLaunch: false);
        Assert.Equal(legacy, oldFactory.Method);
        Assert.Equal(typeof(bool), focusFactory.Method.GetParameters()[0].ParameterType);
        Assert.False(focusFactory.Method.GetParameters()[0].IsOptional);
        Assert.NotNull(defaultLiteral);
        Assert.NotNull(namedFocus);
    }

    [Theory]
    [InlineData(3840, 2160, 2.5f, 3456, 1944)]   // Windows 4K at 250%: the 2x point cap is 5x in pixels
    [InlineData(2880, 1800, 2f, 2592, 1458)]     // Windows 2880x1800 at 200%: 90% of the screen
    [InlineData(1920, 1080, 1.5f, 1728, 972)]    // Windows 1080p at 150%: unchanged
    [InlineData(3840, 2160, 1f, 2560, 1440)]     // points or unknown units: the legacy 2x cap
    [InlineData(2056, 1329, 1f, 1850, 1041)]     // macOS points: unchanged
    public void The_default_scale_cap_is_in_logical_points(int screenWidth, int screenHeight,
        float coordinatesPerPoint, int width, int height)
    {
        var created = AppWindow.CreateScaled(() => new ScreenMetrics(screenWidth, screenHeight, coordinatesPerPoint),
            Capture, "dpi", 1280, 720);

        Assert.Equal(width, created.Width);
        Assert.Equal(height, created.Height);
    }

    [Theory]
    [InlineData(DisplayScale.GlfwPlatformWin32, 2.5f, 2.5f)]
    [InlineData(DisplayScale.GlfwPlatformX11, 1.25f, 1.25f)]
    [InlineData(DisplayScale.GlfwPlatformCocoa, 2f, 1f)]
    [InlineData(DisplayScale.GlfwPlatformWayland, 2f, 1f)]
    [InlineData(0x00060005, 2f, 1f)]                       // GLFW null platform
    [InlineData(0, 1.5f, 1f)]                              // unknown platform
    [InlineData(DisplayScale.GlfwPlatformWin32, float.NaN, 1f)]
    [InlineData(DisplayScale.GlfwPlatformWin32, 0f, 1f)]
    [InlineData(DisplayScale.GlfwPlatformX11, float.PositiveInfinity, 1f)]
    public void Only_pixel_window_coordinates_convert_points_through_the_monitor_scale(int glfwPlatform,
        float contentScale, float coordinatesPerPoint)
    {
        Assert.Equal(coordinatesPerPoint, DisplayScale.CoordinatesPerPoint(glfwPlatform, contentScale));
    }

    static ScreenMetrics Screen(int width, int height) => new(width, height, 1f);

    static Created Capture(string title, int width, int height, PresentMode present,
        FrameCap cap, GpuBackendKind? backend, bool focus)
        => new(title, width, height, present, cap, backend,
            WindowLaunch.Resolve(InitialMonitor.Saved, focus, null, null).Hints);

    sealed record Created(string Title, int Width, int Height, PresentMode PresentMode,
        FrameCap FrameCap, GpuBackendKind? Backend, WindowCreationHints Hints);
}
