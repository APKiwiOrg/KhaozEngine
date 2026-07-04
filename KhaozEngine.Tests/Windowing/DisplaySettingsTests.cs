using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

/// <summary>
/// The runtime display-settings surface: the pure window-mode policy (<see cref="WindowModePlanner"/>), the
/// Metal-vsync-needs-a-cap predicate, and the <see cref="DisplaySettings"/> snapshot value. All headless - the
/// live swapchain / Silk-window side is exercised by the GPU present-mode test and the windowed smoke sample.
/// </summary>
public class DisplaySettingsTests
{
    // ---- WindowModePlanner: pure WindowMode -> concrete window state policy ----

    [Fact]
    public void Windowed_keeps_a_resizable_border_and_the_windowed_size_without_moving()
    {
        var plan = WindowModePlanner.Compute(WindowMode.Windowed,
            monitorX: 0, monitorY: 0, monitorWidth: 2560, monitorHeight: 1440,
            windowedWidth: 1280, windowedHeight: 720);

        Assert.Equal(WindowStateTarget.Normal, plan.State);
        Assert.Equal(WindowBorderTarget.Resizable, plan.Border);
        Assert.True(plan.SetSize);
        Assert.Equal(1280, plan.Width);
        Assert.Equal(720, plan.Height);
        Assert.False(plan.SetPosition); // leave the window where the user put it
    }

    [Fact]
    public void BorderlessFullscreen_covers_the_monitor_at_its_origin_with_no_border()
    {
        var plan = WindowModePlanner.Compute(WindowMode.BorderlessFullscreen,
            monitorX: 100, monitorY: 50, monitorWidth: 2560, monitorHeight: 1440,
            windowedWidth: 1280, windowedHeight: 720);

        Assert.Equal(WindowStateTarget.Normal, plan.State);
        Assert.Equal(WindowBorderTarget.Hidden, plan.Border);
        Assert.True(plan.SetPosition);
        Assert.Equal(100, plan.X);
        Assert.Equal(50, plan.Y);
        Assert.True(plan.SetSize);
        Assert.Equal(2560, plan.Width);
        Assert.Equal(1440, plan.Height);
    }

    [Fact]
    public void BorderlessFullscreen_without_a_known_monitor_falls_back_to_the_windowed_size_and_does_not_move()
    {
        // Headless / no display: monitor bounds unknown (0). Must not produce a zero-size window.
        var plan = WindowModePlanner.Compute(WindowMode.BorderlessFullscreen,
            monitorX: 0, monitorY: 0, monitorWidth: 0, monitorHeight: 0,
            windowedWidth: 1024, windowedHeight: 640);

        Assert.Equal(WindowStateTarget.Normal, plan.State);
        Assert.Equal(WindowBorderTarget.Hidden, plan.Border);
        Assert.False(plan.SetPosition);
        Assert.True(plan.SetSize);
        Assert.Equal(1024, plan.Width);
        Assert.Equal(640, plan.Height);
    }

    [Fact]
    public void Windowed_restores_the_known_windowed_position_when_asked()
    {
        var plan = WindowModePlanner.Compute(WindowMode.Windowed,
            monitorX: 0, monitorY: 0, monitorWidth: 2560, monitorHeight: 1440,
            windowedWidth: 1280, windowedHeight: 720,
            restoreWindowedPos: true, windowedX: 200, windowedY: 150);

        Assert.True(plan.SetPosition);
        Assert.Equal(200, plan.X);
        Assert.Equal(150, plan.Y);
        Assert.True(plan.SetSize);
        Assert.Equal(1280, plan.Width);
    }

    [Fact]
    public void ExclusiveFullscreen_uses_the_fullscreen_state_and_leaves_geometry_to_the_os()
    {
        var plan = WindowModePlanner.Compute(WindowMode.ExclusiveFullscreen,
            monitorX: 0, monitorY: 0, monitorWidth: 2560, monitorHeight: 1440,
            windowedWidth: 1280, windowedHeight: 720);

        Assert.Equal(WindowStateTarget.Fullscreen, plan.State);
        Assert.False(plan.SetPosition);
        Assert.False(plan.SetSize);
    }

    // ---- Metal vsync warning predicate ----

    [Fact]
    public void Metal_vsync_with_no_frame_cap_wants_a_frame_cap_warning()
    {
        Assert.True(DisplaySettings.RequiresFrameCapWarning(GpuBackendKind.Metal, PresentMode.Vsync, frameCapHz: 0));
    }

    [Theory]
    [InlineData(GpuBackendKind.Metal, PresentMode.Vsync, 60)]      // capped -> deterministic already
    [InlineData(GpuBackendKind.Metal, PresentMode.Immediate, 0)]  // not vsync
    [InlineData(GpuBackendKind.Direct3D11, PresentMode.Vsync, 0)] // D3D11 vsync really caps
    [InlineData(GpuBackendKind.Vulkan, PresentMode.Vsync, 0)]     // Vulkan FIFO really caps
    public void Other_configs_do_not_warn(GpuBackendKind backend, PresentMode mode, int cap)
    {
        Assert.False(DisplaySettings.RequiresFrameCapWarning(backend, mode, cap));
    }

    // ---- DisplaySettings snapshot value ----

    [Fact]
    public void DisplaySettings_round_trips_by_value_and_with_expression()
    {
        var a = new DisplaySettings(PresentMode.Vsync, 60, WindowMode.Windowed, 1280, 720);
        var b = a with { PresentMode = PresentMode.Immediate, FrameCapHz = 0 };

        Assert.Equal(new DisplaySettings(PresentMode.Vsync, 60, WindowMode.Windowed, 1280, 720), a);
        Assert.Equal(PresentMode.Immediate, b.PresentMode);
        Assert.Equal(0, b.FrameCapHz);
        Assert.Equal(WindowMode.Windowed, b.WindowMode); // unchanged fields survive `with`
        Assert.Equal(1280, b.Width);
        Assert.NotEqual(a, b);
    }
}
