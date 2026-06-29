using System.Linq;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class DiagnosticsOverlayTests
{
    static string ValueOf(OverlaySection s, string label) =>
        s.Rows.First(r => r.Label == label).Value;

    static bool HasRow(OverlaySection s, string label) =>
        s.Rows.Any(r => r.Label == label);

    [Fact]
    public void Toggle_flips_visibility()
    {
        var o = new DiagnosticsOverlay();
        Assert.False(o.Visible);
        o.Toggle();
        Assert.True(o.Visible);
        o.Toggle();
        Assert.False(o.Visible);
    }

    [Fact]
    public void Toggle_key_press_flips_visible_and_returns_state()
    {
        var o = new DiagnosticsOverlay();

        bool vis = o.Update(OverlayTestInput.KeyFrame(Key.F1), 0.016f);
        Assert.True(vis);
        Assert.True(o.Visible);

        vis = o.Update(OverlayTestInput.KeyFrame(Key.F1), 0.016f);
        Assert.False(vis);
        Assert.False(o.Visible);
    }

    [Fact]
    public void Empty_input_does_not_toggle()
    {
        var o = new DiagnosticsOverlay { Visible = true };
        o.Update(InputState.Empty, 0.016f);
        Assert.True(o.Visible);

        var o2 = new DiagnosticsOverlay();
        o2.Update(InputState.Empty, 0.016f);
        Assert.False(o2.Visible);
    }

    [Fact]
    public void Only_the_themed_toggle_key_toggles()
    {
        var o = new DiagnosticsOverlay(new DiagnosticsOverlayTheme { ToggleKey = Key.F3 });

        o.Update(OverlayTestInput.KeyFrame(Key.F1), 0.016f); // wrong key
        Assert.False(o.Visible);

        o.Update(OverlayTestInput.KeyFrame(Key.F3), 0.016f); // themed key
        Assert.True(o.Visible);
    }

    [Fact]
    public void Gamepad_button_toggles_when_bound()
    {
        var o = new DiagnosticsOverlay(new DiagnosticsOverlayTheme { TriggerButton = GamepadButton.Back });
        o.Update(OverlayTestInput.PadFrame(GamepadButton.Back), 0.016f);
        Assert.True(o.Visible);
    }

    [Fact]
    public void Fade_advances_toward_visible()
    {
        var o = new DiagnosticsOverlay { Visible = true };
        o.Update(InputState.Empty, 0.1f);
        Assert.True(o.Alpha > 0f);
    }

    [Fact]
    public void Fade_disabled_snaps_alpha_in_one_update()
    {
        var o = new DiagnosticsOverlay(new DiagnosticsOverlayTheme { FadeSpeed = 0f }) { Visible = true };
        o.Update(InputState.Empty, 0.016f);
        Assert.Equal(1f, o.Alpha, 3);

        o.Visible = false;
        o.Update(InputState.Empty, 0.016f);
        Assert.Equal(0f, o.Alpha, 3);
    }

    [Fact]
    public void PerformanceSection_titled_and_lists_fps()
    {
        var f = new FrameStats();
        for (int i = 0; i < 120; i++) f.Sample(1f / 60f);

        OverlaySection s = DiagnosticsOverlay.PerformanceSection(f);
        Assert.Equal("Performance", s.Title);
        Assert.True(HasRow(s, "fps"));
        Assert.Equal("60", ValueOf(s, "fps"));
    }

    [Fact]
    public void NetworkSection_shows_not_connected_when_disconnected()
    {
        OverlaySection s = DiagnosticsOverlay.NetworkSection(new ClientNetStats { Connected = false });
        Assert.Equal("Network", s.Title);
        Assert.Single(s.Rows);
        Assert.Contains("not connected", s.Rows[0].Value);
    }

    [Fact]
    public void NetworkSection_lists_ping_when_connected()
    {
        var n = new ClientNetStats { Connected = true, RttMs = 48f, PacketLoss = 0.05f, SnapshotsPerSec = 30f };
        OverlaySection s = DiagnosticsOverlay.NetworkSection(in n);
        Assert.True(HasRow(s, "ping"));
        Assert.Contains("48", ValueOf(s, "ping"));
    }
}
