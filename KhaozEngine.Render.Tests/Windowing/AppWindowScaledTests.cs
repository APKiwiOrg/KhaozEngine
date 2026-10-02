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
        var created = AppWindow.CreateScaled(() => (1920, 1080), Capture, "scaled", 640, 360,
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
        var created = AppWindow.CreateScaled(() => (3840, 2160), Capture, "legacy", 640, 360);

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
        var created = AppWindow.CreateScaled(() => (0, 0), Capture, "headless", 800, 600,
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
        var grown = AppWindow.CreateScaled(() => (1920, 1080), Capture, "grown", 640, 360, maxScale: 4f);
        Assert.Equal(1728, grown.Width);
        Assert.Equal(972, grown.Height);

        var small = AppWindow.CreateScaled(() => (320, 240), Capture, "small", 640, 360);
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

    static Created Capture(string title, int width, int height, PresentMode present,
        FrameCap cap, GpuBackendKind? backend, bool focus)
        => new(title, width, height, present, cap, backend,
            WindowLaunch.Resolve(InitialMonitor.Saved, focus, null, null).Hints);

    sealed record Created(string Title, int Width, int Height, PresentMode PresentMode,
        FrameCap FrameCap, GpuBackendKind? Backend, WindowCreationHints Hints);
}
