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
}
