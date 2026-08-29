using KhaozEngine.Gui;
using KhaozEngine.Render2D;
using KhaozEngine.Tests.Updates; // FakeUpdateStatus
using KhaozEngine.Updates;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class UpdateOverlayScreenTests
{
    // Stand-in for a game screen below the overlay (e.g. Nullwake's mining sim). Records whether it was
    // updated at all this frame and whether it was allowed to act on input.
    sealed class RecordingScreen : Screen
    {
        public bool Updated;
        public bool ReceivedInput;
        public void Reset() { Updated = false; ReceivedInput = false; }
        public override bool Update(float dt, bool receivesInput)
        {
            Updated = true;
            ReceivedInput = receivesInput;
            return false; // a passive game screen consumes nothing
        }
        public override void Draw(SpriteBatch batch) { }
    }

    // font/white are unused by Update (only Draw needs them); pass null!.
    static UpdateOverlayScreen NewScreen(IUpdateStatus status) =>
        new(status, null!, null!, new DesignViewport(960, 540));

    [Theory]
    [InlineData(UpdateState.UpdateAvailable)]
    [InlineData(UpdateState.Downloading)]
    [InlineData(UpdateState.ReadyToApply)]
    [InlineData(UpdateState.Failed)]
    public void Optional_update_is_non_modal_so_the_game_keeps_updating(UpdateState state)
    {
        var status = new FakeUpdateStatus { State = state, IsRequired = false };
        var screen = NewScreen(status);
        var stack = new ScreenStack();
        stack.Add(screen);

        stack.Update(0.016f, InputState.Empty);

        // Non-modal: PassUpdateThrough true, so ScreenStack keeps updating the screens below.
        Assert.True(screen.PassUpdateThrough);
    }

    [Theory]
    [InlineData(UpdateState.UpdateAvailable)]
    [InlineData(UpdateState.Downloading)]
    [InlineData(UpdateState.ReadyToApply)]
    [InlineData(UpdateState.Failed)]
    public void Required_update_is_modal(UpdateState state)
    {
        var status = new FakeUpdateStatus { State = state, IsRequired = true };
        var screen = NewScreen(status);
        var stack = new ScreenStack();
        stack.Add(screen);

        stack.Update(0.016f, InputState.Empty);

        Assert.False(screen.PassUpdateThrough); // required: block the game below
    }

    [Fact]
    public void Applying_is_modal_even_when_optional()
    {
        var status = new FakeUpdateStatus { State = UpdateState.Applying, IsRequired = false };
        var screen = NewScreen(status);
        var stack = new ScreenStack();
        stack.Add(screen);

        stack.Update(0.016f, InputState.Empty);

        Assert.False(screen.PassUpdateThrough); // process is relaunching; freeze the game
    }

    [Fact]
    public void Idle_is_non_modal_and_never_triggers()
    {
        var status = new FakeUpdateStatus { State = UpdateState.Idle };
        var screen = NewScreen(status);
        int fired = 0;
        screen.Triggered += () => fired++;
        var stack = new ScreenStack();
        stack.Add(screen);

        stack.Update(0.016f, OverlayTestInput.KeyFrame(Key.U));

        Assert.True(screen.PassUpdateThrough);
        Assert.Equal(0, fired); // hidden: the key does nothing
    }

    [Fact]
    public void Optional_prompt_lets_game_input_through_but_still_fires_the_trigger()
    {
        var status = new FakeUpdateStatus { State = UpdateState.UpdateAvailable, IsRequired = false };
        var overlay = NewScreen(status);
        int fired = 0;
        overlay.Triggered += () => fired++;
        var game = new RecordingScreen { DrawOrder = 100 }; // below the overlay (DrawOrder 10_000)

        var stack = new ScreenStack();
        stack.Add(game);
        stack.Add(overlay);

        // A non-trigger key: the optional overlay consumes nothing, so the game screen updates AND is
        // allowed to act on the input.
        game.Reset();
        stack.Update(0.016f, OverlayTestInput.KeyFrame(Key.Space));
        Assert.True(game.Updated);
        Assert.True(game.ReceivedInput);
        Assert.Equal(0, fired);

        // The trigger key: the overlay fires (starts the download) and consumes only THIS frame, so the
        // game still updates but does not also act on the trigger press.
        game.Reset();
        stack.Update(0.016f, OverlayTestInput.KeyFrame(Key.U));
        Assert.Equal(1, fired);
        Assert.True(game.Updated);
        Assert.False(game.ReceivedInput);
    }

    [Fact]
    public void Gamepad_trigger_starts_the_download_when_optional()
    {
        var status = new FakeUpdateStatus { State = UpdateState.UpdateAvailable, IsRequired = false };
        var overlay = NewScreen(status);
        int fired = 0;
        overlay.Triggered += () => fired++;
        var stack = new ScreenStack();
        stack.Add(overlay);

        stack.Update(0.016f, OverlayTestInput.PadFrame(GamepadButton.Y));

        Assert.Equal(1, fired);
    }

    [Fact]
    public void Dismissing_consumes_only_that_frame_then_leaves_the_game_alone()
    {
        var status = new FakeUpdateStatus { State = UpdateState.Failed, IsRequired = false };
        var overlay = NewScreen(status);
        var game = new RecordingScreen { DrawOrder = 100 };
        var stack = new ScreenStack();
        stack.Add(game);
        stack.Add(overlay);

        // The dismiss press closes the overlay and is consumed, so it does not also open the pause menu.
        game.Reset();
        stack.Update(0.016f, OverlayTestInput.KeyFrame(Key.Escape));
        Assert.True(game.Updated);
        Assert.False(game.ReceivedInput);

        // From the next frame on the overlay is gone and Escape belongs to the game again.
        game.Reset();
        stack.Update(0.016f, OverlayTestInput.KeyFrame(Key.Escape));
        Assert.True(game.Updated);
        Assert.True(game.ReceivedInput);
    }

    [Fact]
    public void A_dismissed_failed_panel_stops_firing_the_trigger()
    {
        var status = new FakeUpdateStatus { State = UpdateState.Failed, IsRequired = false };
        var overlay = NewScreen(status);
        int fired = 0;
        overlay.Triggered += () => fired++;
        var stack = new ScreenStack();
        stack.Add(overlay);

        stack.Update(0.016f, OverlayTestInput.KeyFrame(Key.Escape));
        stack.Update(0.016f, OverlayTestInput.KeyFrame(Key.U));

        Assert.Equal(0, fired); // no retry: the player is out of the cycle
    }

    [Fact]
    public void Required_prompt_starves_the_game_below()
    {
        var status = new FakeUpdateStatus { State = UpdateState.UpdateAvailable, IsRequired = true };
        var overlay = NewScreen(status);
        var game = new RecordingScreen { DrawOrder = 100 };
        var stack = new ScreenStack();
        stack.Add(game);
        stack.Add(overlay);

        game.Reset();
        stack.Update(0.016f, OverlayTestInput.KeyFrame(Key.U));

        Assert.False(overlay.PassUpdateThrough);
        Assert.False(game.Updated); // modal: ScreenStack stops before reaching the game
    }
}
