using System.Collections.Generic;
using KhaozEngine.Game;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

/// <summary>
/// The pure launch-placement policy: the <see cref="WindowLaunch"/> environment parsing and precedence, the
/// <see cref="WindowCreationHints"/> the window is created with, the <see cref="InitialMonitor"/> resolution against
/// a monitor list, and the <see cref="LaunchPlacement.PlacementOverridden"/> flag a game reads to skip persisting
/// its window position. Fully headless: the window itself cannot be created without a display, so everything the
/// engine DECIDES lives in these pure types and everything it TOUCHES is the two GLFW hint calls in
/// <c>AppWindow.Launch.cs</c>.
/// </summary>
public class WindowLaunchTests
{
    // A three-monitor desk with the primary in the middle, so rightmost / leftmost / primary are all different
    // answers and an index is not the same as any of them.
    static IReadOnlyList<MonitorInfo> ThreeMonitors() => new[]
    {
        new MonitorInfo(0, "Primary", 0, 0, 1920, 1080),
        new MonitorInfo(1, "Right", 1920, 100, 2560, 1440),
        new MonitorInfo(2, "Left", -1280, 0, 1280, 1024),
    };

    [Fact]
    public void Rightmost_and_leftmost_read_the_x_origin_and_empty_is_minus_one()
    {
        Assert.Equal(1, WindowPlacement.RightmostIndex(ThreeMonitors()));
        Assert.Equal(2, WindowPlacement.LeftmostIndex(ThreeMonitors()));
        Assert.Equal(-1, WindowPlacement.RightmostIndex(new List<MonitorInfo>()));
        Assert.Equal(-1, WindowPlacement.LeftmostIndex(new List<MonitorInfo>()));
    }

    [Fact]
    public void Rightmost_prefers_the_origin_over_the_right_edge_and_breaks_ties_on_the_lower_index()
    {
        // A narrow monitor parked right of a wide one is still "the one on the right", even though the wide
        // monitor's right EDGE reaches further.
        var monitors = new[]
        {
            new MonitorInfo(0, "Wide", 0, 0, 3840, 1600),
            new MonitorInfo(1, "Narrow", 3000, 0, 1280, 1024),
        };
        Assert.Equal(1, WindowPlacement.RightmostIndex(monitors));

        var stacked = new[]
        {
            new MonitorInfo(0, "Top", 0, 0, 1920, 1080),
            new MonitorInfo(1, "Bottom", 0, 1080, 1920, 1080),
        };
        Assert.Equal(0, WindowPlacement.RightmostIndex(stacked));
        Assert.Equal(0, WindowPlacement.LeftmostIndex(stacked));
    }

    [Fact]
    public void InitialMonitor_resolves_each_kind_against_the_live_list()
    {
        IReadOnlyList<MonitorInfo> monitors = ThreeMonitors();
        Assert.Equal(-1, InitialMonitor.Saved.Resolve(monitors));
        Assert.Equal(0, InitialMonitor.Primary.Resolve(monitors));
        Assert.Equal(1, InitialMonitor.Rightmost.Resolve(monitors));
        Assert.Equal(2, InitialMonitor.Leftmost.Resolve(monitors));
        Assert.Equal(2, InitialMonitor.At(2).Resolve(monitors));
    }

    [Fact]
    public void InitialMonitor_resolves_to_no_move_for_a_monitor_that_is_not_there()
    {
        Assert.Equal(-1, InitialMonitor.At(7).Resolve(ThreeMonitors()));            // unplugged since last launch
        Assert.Equal(-1, InitialMonitor.Rightmost.Resolve(new List<MonitorInfo>())); // headless
        Assert.Equal(-1, InitialMonitor.Primary.Resolve(new List<MonitorInfo>()));
        Assert.True(InitialMonitor.At(-1).IsSaved);                                  // a negative index asks for nothing
    }

    [Fact]
    public void Default_initial_monitor_is_saved_so_a_zero_initialised_options_struct_moves_nothing()
    {
        Assert.True(default(InitialMonitor).IsSaved);
        Assert.Equal(InitialMonitorKind.Saved, default(InitialMonitor).Kind);
        Assert.Equal(InitialMonitor.Saved, default(InitialMonitor));
    }

    [Theory]
    [InlineData("rightmost", InitialMonitorKind.Rightmost, 0)]
    [InlineData("RIGHTMOST", InitialMonitorKind.Rightmost, 0)]
    [InlineData("  leftmost  ", InitialMonitorKind.Leftmost, 0)]
    [InlineData("Primary", InitialMonitorKind.Primary, 0)]
    [InlineData("0", InitialMonitorKind.Index, 0)]
    [InlineData("2", InitialMonitorKind.Index, 2)]
    public void Monitor_env_accepts_every_documented_value(string value, InitialMonitorKind kind, int index)
    {
        Assert.True(WindowLaunch.TryParseMonitor(value, out InitialMonitor monitor, out string? bad));
        Assert.Equal(kind, monitor.Kind);
        Assert.Equal(index, monitor.Index);
        Assert.Null(bad);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Monitor_env_unset_or_blank_is_not_an_override_and_warns_about_nothing(string? value)
    {
        Assert.False(WindowLaunch.TryParseMonitor(value, out InitialMonitor monitor, out string? bad));
        Assert.True(monitor.IsSaved);
        Assert.Null(bad);
    }

    [Theory]
    [InlineData("rihgtmost")]
    [InlineData("-1")]
    [InlineData("second")]
    [InlineData("1.5")]
    public void Monitor_env_garbage_is_ignored_and_comes_back_verbatim_for_the_log_line(string value)
    {
        Assert.False(WindowLaunch.TryParseMonitor(value, out InitialMonitor monitor, out string? bad));
        Assert.True(monitor.IsSaved);
        Assert.Equal(value, bad);
        Assert.Contains(value, WindowLaunch.UnrecognizedMonitorWarning(value));
        Assert.Contains(WindowLaunch.MonitorVar, WindowLaunch.UnrecognizedMonitorWarning(value));
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("No", false)]
    [InlineData(" OFF ", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    public void Focus_env_accepts_every_documented_value(string value, bool expected)
    {
        Assert.True(WindowLaunch.TryParseFocus(value, out bool focus, out string? bad));
        Assert.Equal(expected, focus);
        Assert.Null(bad);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("2")]
    public void Focus_env_garbage_is_ignored_and_comes_back_verbatim_for_the_log_line(string value)
    {
        Assert.False(WindowLaunch.TryParseFocus(value, out bool _, out string? bad));
        Assert.Equal(value, bad);
        Assert.Contains(value, WindowLaunch.UnrecognizedFocusWarning(value));
        Assert.Contains(WindowLaunch.FocusVar, WindowLaunch.UnrecognizedFocusWarning(value));
    }

    [Fact]
    public void Hints_carry_the_focus_decision_to_both_glfw_hints()
    {
        Assert.Equal(new WindowCreationHints(true, true), WindowLaunch.HintsFor(true));
        Assert.Equal(new WindowCreationHints(false, false), WindowLaunch.HintsFor(false));
    }

    [Fact]
    public void Resolve_with_no_environment_is_the_consumer_option_and_no_override()
    {
        LaunchPlacement p = WindowLaunch.Resolve(InitialMonitor.Rightmost, focusOnLaunch: true,
            monitorEnv: null, focusEnv: null);

        Assert.Equal(InitialMonitor.Rightmost, p.Monitor);
        Assert.Equal(new WindowCreationHints(true, true), p.Hints);
        Assert.False(p.PlacementOverridden);
        Assert.Null(p.UnrecognizedMonitorValue);
        Assert.Null(p.UnrecognizedFocusValue);
    }

    [Fact]
    public void Resolve_defaults_to_no_move_and_focus_when_nothing_asks_for_anything()
    {
        LaunchPlacement p = WindowLaunch.Resolve(InitialMonitor.Saved, focusOnLaunch: true, null, null);

        Assert.True(p.Monitor.IsSaved);
        Assert.True(p.Hints.Focused);
        Assert.True(p.Hints.FocusOnShow);
        Assert.False(p.PlacementOverridden);
    }

    [Fact]
    public void Resolve_lets_the_environment_beat_the_consumer_option_and_raises_the_override_flag()
    {
        LaunchPlacement p = WindowLaunch.Resolve(InitialMonitor.Primary, focusOnLaunch: true,
            monitorEnv: "rightmost", focusEnv: "0");

        Assert.Equal(InitialMonitor.Rightmost, p.Monitor);
        Assert.Equal(new WindowCreationHints(false, false), p.Hints);
        Assert.True(p.PlacementOverridden);
    }

    [Fact]
    public void Resolve_lets_the_consumer_option_turn_focus_off_without_any_environment()
    {
        LaunchPlacement p = WindowLaunch.Resolve(InitialMonitor.Saved, focusOnLaunch: false, null, null);

        Assert.Equal(new WindowCreationHints(false, false), p.Hints);
        Assert.False(p.PlacementOverridden); // focus is not a PLACEMENT override: the saved position is untouched
    }

    [Fact]
    public void A_garbage_monitor_override_leaves_the_option_in_charge_and_does_not_raise_the_flag()
    {
        // The flag gates whether the game persists its window position, so a typo must not switch it on: the
        // window is wherever the game put it, and that is still worth saving.
        LaunchPlacement p = WindowLaunch.Resolve(InitialMonitor.Leftmost, focusOnLaunch: true,
            monitorEnv: "rihgtmost", focusEnv: "maybe");

        Assert.Equal(InitialMonitor.Leftmost, p.Monitor);
        Assert.False(p.PlacementOverridden);
        Assert.Equal("rihgtmost", p.UnrecognizedMonitorValue);
        Assert.Equal("maybe", p.UnrecognizedFocusValue);
        Assert.Equal(new WindowCreationHints(true, true), p.Hints); // garbage focus keeps the consumer's value
    }

    [Fact]
    public void An_index_override_resolves_through_to_the_monitor_it_names()
    {
        LaunchPlacement p = WindowLaunch.Resolve(InitialMonitor.Saved, focusOnLaunch: true,
            monitorEnv: "1", focusEnv: null);

        Assert.True(p.PlacementOverridden);
        Assert.Equal(1, p.Monitor.Resolve(ThreeMonitors()));
    }

    [Fact]
    public void GameAppOptions_default_to_a_focused_window_the_engine_does_not_move()
    {
        GameAppOptions built = GameAppOptions.For("t", 640, 480);
        Assert.Null(built.FocusOnLaunch);          // null reads as true at the window ctor
        Assert.True(built.InitialMonitor.IsSaved);

        GameAppOptions raw = default;              // a hand-rolled struct gets the same behaviour
        Assert.Null(raw.FocusOnLaunch);
        Assert.True(raw.InitialMonitor.IsSaved);
    }

    [Fact]
    public void The_documented_env_var_names_are_what_the_engine_reads()
    {
        Assert.Equal("KE_WINDOW_MONITOR", WindowLaunch.MonitorVar);
        Assert.Equal("KE_WINDOW_FOCUS", WindowLaunch.FocusVar);
    }
}
