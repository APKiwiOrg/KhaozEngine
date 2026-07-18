using System.Collections.Generic;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

/// <summary>
/// The pure window-placement policy (<see cref="WindowPlacement"/>): which monitor a window rect belongs to,
/// centring a window on a monitor, and clamping a window back on-screen. Fully headless (no Silk / GPU),
/// mirroring the <see cref="WindowModePlanner"/> tests.
/// </summary>
public class WindowPlacementTests
{
    static IReadOnlyList<MonitorInfo> TwoMonitors() => new[]
    {
        new MonitorInfo(0, "Primary",  0, 0, 1920, 1080),
        new MonitorInfo(1, "Right", 1920, 0, 2560, 1440),
    };

    [Fact]
    public void MonitorIndexFor_returns_the_monitor_containing_the_window_center()
    {
        Assert.Equal(0, WindowPlacement.MonitorIndexFor(100, 100, 1280, 720, TwoMonitors()));
        Assert.Equal(1, WindowPlacement.MonitorIndexFor(2100, 100, 1280, 720, TwoMonitors()));
    }

    [Fact]
    public void MonitorIndexFor_off_all_monitors_picks_the_nearest_and_empty_is_minus_one()
    {
        Assert.Equal(1, WindowPlacement.MonitorIndexFor(5000, 100, 100, 100, TwoMonitors())); // nearest = right
        Assert.Equal(-1, WindowPlacement.MonitorIndexFor(0, 0, 100, 100, new List<MonitorInfo>()));
    }

    [Fact]
    public void CenterOn_centers_within_the_monitor_bounds_including_offset()
    {
        Assert.Equal((320, 180), WindowPlacement.CenterOn(new MonitorInfo(0, "m", 0, 0, 1920, 1080), 1280, 720));
        Assert.Equal((2240, 180), WindowPlacement.CenterOn(new MonitorInfo(1, "m", 1920, 0, 1920, 1080), 1280, 720));
    }

    [Fact]
    public void ClampVisible_leaves_an_already_visible_window_untouched()
    {
        Assert.Equal((100, 100), WindowPlacement.ClampVisible(100, 100, 1280, 720, TwoMonitors()));
    }

    [Fact]
    public void ClampVisible_pulls_an_offscreen_window_back_onto_a_monitor()
    {
        // Saved on a now-gone monitor at x=2600 while only a single 1920x1080 monitor remains.
        var one = new[] { new MonitorInfo(0, "Only", 0, 0, 1920, 1080) };
        var (x, y) = WindowPlacement.ClampVisible(2600, 100, 1280, 720, one);
        Assert.Equal(640, x); // 1920 - 1280
        Assert.Equal(100, y);
    }

    [Fact]
    public void ClampVisible_pins_a_window_larger_than_the_monitor_to_its_origin()
    {
        var one = new[] { new MonitorInfo(0, "Only", 0, 0, 1920, 1080) };
        Assert.Equal((0, 0), WindowPlacement.ClampVisible(3000, 3000, 2560, 1440, one));
    }

    [Fact]
    public void ClampVisible_with_no_monitors_returns_the_input()
    {
        Assert.Equal((10, 20), WindowPlacement.ClampVisible(10, 20, 800, 600, new List<MonitorInfo>()));
    }
}
