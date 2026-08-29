using KhaozEngine.Gui;
using KhaozEngine.Tests.Updates; // FakeUpdateStatus
using KhaozEngine.Updates;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class UpdateOverlayViewTests
{
    [Theory]
    [InlineData(UpdateState.Idle, false)]
    [InlineData(UpdateState.Checking, false)]
    [InlineData(UpdateState.UpdateAvailable, true)]
    [InlineData(UpdateState.Downloading, true)]
    [InlineData(UpdateState.ReadyToApply, true)]
    [InlineData(UpdateState.Applying, true)]
    [InlineData(UpdateState.Failed, true)]
    public void IsVisible_matches_state(UpdateState s, bool vis) =>
        Assert.Equal(vis, UpdateOverlayView.IsVisible(s));

    [Fact]
    public void Trigger_key_in_visible_state_raises_events_and_consumes()
    {
        var view = new UpdateOverlayView();
        UpdateState? got = null;
        int count = 0;
        view.OnTrigger += s => got = s;
        view.Triggered += () => count++;

        bool consumed = view.Update(new FakeUpdateStatus { State = UpdateState.UpdateAvailable },
            OverlayTestInput.KeyFrame(Key.U), 0.016f);

        Assert.True(consumed);
        Assert.Equal(UpdateState.UpdateAvailable, got);
        Assert.Equal(1, count);
    }

    [Fact]
    public void No_trigger_and_no_consume_in_hidden_state()
    {
        var view = new UpdateOverlayView();
        bool fired = false;
        view.Triggered += () => fired = true;

        bool consumed = view.Update(new FakeUpdateStatus { State = UpdateState.Idle },
            OverlayTestInput.KeyFrame(Key.U), 0.016f);

        Assert.False(consumed);
        Assert.False(fired);
    }

    [Fact]
    public void Gamepad_button_triggers()
    {
        var view = new UpdateOverlayView();
        int count = 0;
        view.Triggered += () => count++;

        view.Update(new FakeUpdateStatus { State = UpdateState.ReadyToApply },
            OverlayTestInput.PadFrame(GamepadButton.Y), 0.016f);

        Assert.Equal(1, count);
    }

    [Fact]
    public void Wrong_key_does_not_trigger()
    {
        var view = new UpdateOverlayView();
        bool fired = false;
        view.Triggered += () => fired = true;

        view.Update(new FakeUpdateStatus { State = UpdateState.UpdateAvailable },
            OverlayTestInput.KeyFrame(Key.J), 0.016f);

        Assert.False(fired);
    }

    [Fact]
    public void Fade_advances_toward_visible()
    {
        var view = new UpdateOverlayView();
        view.Update(new FakeUpdateStatus { State = UpdateState.UpdateAvailable }, InputState.Empty, 0.1f);
        Assert.True(view.Alpha > 0f);
    }

    [Theory]
    [InlineData(0, 100, 0f)]
    [InlineData(50, 100, 0.5f)]
    [InlineData(150, 100, 1f)]
    [InlineData(10, 0, 0f)]
    public void ProgressFraction_clamps(long done, long total, float expected) =>
        Assert.Equal(expected,
            UpdateOverlayView.ProgressFraction(new FakeUpdateStatus { BytesDownloaded = done, TotalDownloadBytes = total }), 3);

    // --- Dismiss (#739) ---

    [Theory]
    [InlineData(UpdateState.Idle, false)]
    [InlineData(UpdateState.Checking, false)]
    [InlineData(UpdateState.UpdateAvailable, true)]
    [InlineData(UpdateState.Downloading, false)]   // in flight: the player already started it
    [InlineData(UpdateState.ReadyToApply, true)]
    [InlineData(UpdateState.Applying, false)]      // the process is about to exit
    [InlineData(UpdateState.Failed, true)]
    public void IsDismissible_matches_state(UpdateState s, bool dismissible) =>
        Assert.Equal(dismissible, UpdateOverlayView.IsDismissible(s));

    [Fact]
    public void Dismiss_key_hides_a_failed_panel_and_the_trigger_stops_firing()
    {
        var view = new UpdateOverlayView();
        var status = new FakeUpdateStatus { State = UpdateState.Failed };
        UpdateState? declined = null;
        int triggers = 0;
        view.OnDismiss += s => declined = s;
        view.Triggered += () => triggers++;

        // The panel is up, then the player presses the dismiss key.
        Assert.True(view.Update(status, InputState.Empty, 0.016f));
        bool stillShowing = view.Update(status, OverlayTestInput.KeyFrame(Key.Escape), 0.016f);

        Assert.True(stillShowing);            // reported for the frame the press landed on
        Assert.Equal(UpdateState.Failed, declined);
        Assert.True(view.DismissedThisFrame);
        Assert.False(view.IsShowing(status)); // and it is gone from here on

        // The retry key no longer reaches the service: this is the exit from the retry cycle.
        Assert.False(view.Update(status, OverlayTestInput.KeyFrame(Key.U), 0.016f));
        Assert.Equal(0, triggers);
    }

    [Fact]
    public void Gamepad_dismiss_button_also_dismisses()
    {
        var view = new UpdateOverlayView();
        var status = new FakeUpdateStatus { State = UpdateState.UpdateAvailable };

        view.Update(status, OverlayTestInput.PadFrame(GamepadButton.B), 0.016f);

        Assert.True(view.IsDismissed(UpdateState.UpdateAvailable));
    }

    [Fact]
    public void A_dismissal_survives_the_state_being_reported_again()
    {
        var view = new UpdateOverlayView();
        var status = new FakeUpdateStatus { State = UpdateState.UpdateAvailable };
        view.Update(status, OverlayTestInput.KeyFrame(Key.Escape), 0.016f);

        // A periodic recheck cycles Checking and lands back on the same offer. Re-showing here would be the
        // same nag in slow motion, so the dismissal has to outlive the round trip.
        status.State = UpdateState.Checking;
        view.Update(status, InputState.Empty, 0.016f);
        status.State = UpdateState.UpdateAvailable;

        Assert.False(view.Update(status, InputState.Empty, 0.016f));
    }

    [Fact]
    public void A_state_the_player_has_not_declined_shows_again()
    {
        var view = new UpdateOverlayView();
        var status = new FakeUpdateStatus { State = UpdateState.UpdateAvailable };
        view.Update(status, OverlayTestInput.KeyFrame(Key.Escape), 0.016f);
        Assert.False(view.Update(status, InputState.Empty, 0.016f));

        // The flow advances (a background download finishes): ReadyToApply was never declined, so the panel
        // comes back on its own.
        status.State = UpdateState.ReadyToApply;

        Assert.True(view.Update(status, InputState.Empty, 0.016f));
        Assert.True(view.IsShowing(status));
    }

    [Fact]
    public void A_required_update_refuses_the_dismiss_key()
    {
        var view = new UpdateOverlayView();
        var status = new FakeUpdateStatus { State = UpdateState.UpdateAvailable, IsRequired = true };
        bool declined = false;
        view.OnDismiss += _ => declined = true;

        bool showing = view.Update(status, OverlayTestInput.KeyFrame(Key.Escape), 0.016f);

        Assert.True(showing);
        Assert.False(declined);
        Assert.False(view.CanDismiss(status));
        Assert.True(view.IsShowing(status));
    }

    [Fact]
    public void A_required_update_shows_even_after_a_direct_Dismiss_call()
    {
        var view = new UpdateOverlayView();
        var status = new FakeUpdateStatus { State = UpdateState.ReadyToApply, IsRequired = true };

        view.Dismiss(UpdateState.ReadyToApply);

        Assert.True(view.IsShowing(status));
    }

    [Theory]
    [InlineData(UpdateState.Downloading)]
    [InlineData(UpdateState.Applying)]
    public void An_in_flight_state_refuses_the_dismiss_key(UpdateState state)
    {
        var view = new UpdateOverlayView();
        var status = new FakeUpdateStatus { State = state };

        bool showing = view.Update(status, OverlayTestInput.KeyFrame(Key.Escape), 0.016f);

        Assert.True(showing);
        Assert.False(view.IsDismissed(state));
    }

    [Fact]
    public void ResetDismissed_brings_the_panel_back()
    {
        var view = new UpdateOverlayView();
        var status = new FakeUpdateStatus { State = UpdateState.Failed };
        view.Update(status, OverlayTestInput.KeyFrame(Key.Escape), 0.016f);
        Assert.False(view.IsShowing(status));

        view.ResetDismissed();

        Assert.True(view.IsShowing(status));
        Assert.True(view.CanDismiss(status));
    }

    [Fact]
    public void Without_a_dismiss_press_every_visible_state_still_shows()
    {
        var view = new UpdateOverlayView();
        foreach (UpdateState s in new[]
        {
            UpdateState.UpdateAvailable, UpdateState.Downloading, UpdateState.ReadyToApply,
            UpdateState.Applying, UpdateState.Failed,
        })
        {
            var status = new FakeUpdateStatus { State = s };
            Assert.True(view.Update(status, OverlayTestInput.KeyFrame(Key.U), 0.016f));
            Assert.False(view.DismissedThisFrame);
        }
    }

    [Fact]
    public void The_trigger_wins_a_frame_carrying_both_presses()
    {
        // One InputState cannot hold two keys through the test builder, so rebind the dismiss key onto the
        // trigger key: the frame then carries both bindings at once.
        var view = new UpdateOverlayView(new UpdateOverlayTheme { DismissKey = Key.U, DismissButton = null });
        var status = new FakeUpdateStatus { State = UpdateState.Failed };
        int triggers = 0;
        view.Triggered += () => triggers++;

        view.Update(status, OverlayTestInput.KeyFrame(Key.U), 0.016f);

        Assert.Equal(1, triggers);
        Assert.False(view.DismissedThisFrame);
        Assert.True(view.IsShowing(status));
    }
}
