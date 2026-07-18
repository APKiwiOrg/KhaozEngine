using System;
using KhaozEngine.App;
using KhaozEngine.Gui;
using Xunit;

namespace KhaozEngine.Tests.Connection;

/// <summary>
/// Headless coverage of <see cref="ConnectionStatusController"/>: the escalation threshold, the anti-flicker
/// hold, the drop-timer reset on reconnect, and faithful pass-through of the countdown/attempt/retry/message
/// data. Uses small fixed <c>dt</c> steps rather than wall-clock sleeps.
/// </summary>
public sealed class ConnectionStatusControllerTests
{
    const float Dt = 0.1f;

    static ConnectionStatusView Pump(ConnectionStatusController controller, ConnectionStatusSignals signals, int frames)
    {
        ConnectionStatusView view = default;
        for (int i = 0; i < frames; i++)
            view = controller.Update(signals, Dt);
        return view;
    }

    [Fact]
    public void Connected_shows_nothing()
    {
        var controller = new ConnectionStatusController();
        var signals = new ConnectionStatusSignals { Phase = ConnectionPhase.Connected };

        ConnectionStatusView view = controller.Update(signals, Dt);

        Assert.Equal(ConnectionUiMode.None, view.Mode);
    }

    [Fact]
    public void Planned_update_escalates_to_screen_on_the_first_frame()
    {
        var controller = new ConnectionStatusController();
        var signals = new ConnectionStatusSignals { Phase = ConnectionPhase.Reconnecting, PlannedUpdate = true };

        // No threshold wait: a single frame is enough.
        ConnectionStatusView view = controller.Update(signals, Dt);

        Assert.Equal(ConnectionUiMode.Screen, view.Mode);
        Assert.Equal(ConnectionStatusKind.PlannedUpdate, view.Kind);
    }

    [Fact]
    public void Generic_outage_banners_then_escalates_past_the_threshold()
    {
        var controller = new ConnectionStatusController(); // EscalateAfterSeconds defaults to 6f
        var signals = new ConnectionStatusSignals { Phase = ConnectionPhase.Reconnecting };

        ConnectionStatusView beforeThreshold = Pump(controller, signals, frames: 59); // 5.9s: still a banner
        Assert.Equal(ConnectionUiMode.Banner, beforeThreshold.Mode);

        ConnectionStatusView afterThreshold = Pump(controller, signals, frames: 2); // 6.1s: escalates
        Assert.Equal(ConnectionUiMode.Screen, afterThreshold.Mode);
        Assert.Equal(ConnectionStatusKind.Reconnecting, afterThreshold.Kind);
    }

    [Fact]
    public void Reconnect_past_the_anti_flicker_floor_returns_to_none_and_resets_the_drop_timer()
    {
        var controller = new ConnectionStatusController(); // escalate 6f, floor 1.5f
        var outage = new ConnectionStatusSignals { Phase = ConnectionPhase.Reconnecting };
        Pump(controller, outage, frames: 65); // escalate well past the 6s threshold -> Screen

        var connected = new ConnectionStatusSignals { Phase = ConnectionPhase.Connected };
        ConnectionStatusView view = Pump(controller, connected, frames: 20); // 2s clears the 1.5s floor

        Assert.Equal(ConnectionUiMode.None, view.Mode);

        // The drop timer reset (not merely paused): a fresh outage again takes the FULL EscalateAfterSeconds.
        ConnectionStatusView justUnderThreshold = Pump(controller, outage, frames: 59); // 5.9s
        Assert.Equal(ConnectionUiMode.Banner, justUnderThreshold.Mode);
        ConnectionStatusView pastThreshold = Pump(controller, outage, frames: 2); // 6.1s
        Assert.Equal(ConnectionUiMode.Screen, pastThreshold.Mode);
    }

    [Fact]
    public void Anti_flicker_holds_the_screen_through_a_sub_second_reflap_with_the_cached_outage_data()
    {
        var controller = new ConnectionStatusController(); // floor 1.5s
        var outage = new ConnectionStatusSignals
        {
            Phase = ConnectionPhase.Reconnecting,
            Attempt = 3,
            EtaUtc = new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc),
        };
        Pump(controller, outage, frames: 61); // escalate past 6s -> Screen (screenElapsed still small)

        var connected = new ConnectionStatusSignals { Phase = ConnectionPhase.Connected };
        ConnectionStatusView view = controller.Update(connected, Dt); // one sub-second reflap frame

        // Floor (1.5s) not yet met: still Screen, and the data is the cached outage, not blanked.
        Assert.Equal(ConnectionUiMode.Screen, view.Mode);
        Assert.Equal(3, view.Attempt);
        Assert.Equal(outage.EtaUtc, view.EtaUtc);
    }

    [Fact]
    public void Passes_countdown_and_metadata_through_faithfully()
    {
        var controller = new ConnectionStatusController();
        DateTime future = DateTime.UtcNow.AddMinutes(5);
        var signals = new ConnectionStatusSignals
        {
            Phase = ConnectionPhase.Reconnecting,
            PlannedUpdate = true, // escalates on the first frame regardless of the drop timer
            EtaUtc = future,
            Attempt = 4,
            SecondsUntilRetry = 12.5f,
            MessageId = new StringId("server.maintenance"),
        };

        ConnectionStatusView view = controller.Update(signals, Dt);

        Assert.Equal(future, view.EtaUtc);
        Assert.Equal(4, view.Attempt);
        Assert.Equal(12.5f, view.SecondsUntilRetry);
        Assert.Equal(new StringId("server.maintenance"), view.MessageId);
    }

    [Fact]
    public void Passes_an_expired_eta_through_unclamped_because_the_clamp_lives_in_the_screen()
    {
        var controller = new ConnectionStatusController();
        DateTime past = DateTime.UtcNow.AddMinutes(-1);
        var signals = new ConnectionStatusSignals
        {
            Phase = ConnectionPhase.Reconnecting,
            PlannedUpdate = true,
            EtaUtc = past,
        };

        ConnectionStatusView view = controller.Update(signals, Dt);

        Assert.Equal(past, view.EtaUtc);
    }

    [Fact]
    public void Custom_policy_options_change_the_escalation_threshold()
    {
        var options = new ConnectionStatusPolicyOptions { EscalateAfterSeconds = 2f };
        var controller = new ConnectionStatusController(options);
        var signals = new ConnectionStatusSignals { Phase = ConnectionPhase.Reconnecting };

        ConnectionStatusView justUnder = Pump(controller, signals, frames: 19); // 1.9s
        Assert.Equal(ConnectionUiMode.Banner, justUnder.Mode);

        ConnectionStatusView past = Pump(controller, signals, frames: 2); // 2.1s
        Assert.Equal(ConnectionUiMode.Screen, past.Mode);
    }
}
