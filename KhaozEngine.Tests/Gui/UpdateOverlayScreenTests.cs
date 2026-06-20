using KhaozEngine.Gui;
using KhaozEngine.Tests.Updates; // FakeUpdateStatus
using KhaozEngine.Updates;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class UpdateOverlayScreenTests
{
    [Fact]
    public void Fires_trigger_and_toggles_modality_with_visibility()
    {
        var status = new FakeUpdateStatus { State = UpdateState.Idle };
        // font/white are unused by Update (only Draw needs them); pass null!.
        var screen = new UpdateOverlayScreen(status, null!, null!, new DesignViewport(960, 540));
        int fired = 0;
        screen.Triggered += () => fired++;

        var stack = new ScreenStack();
        stack.Add(screen);

        // Idle: hidden -> passes update through, no trigger even with the key down.
        stack.Update(0.016f, OverlayTestInput.KeyFrame(Key.U));
        Assert.True(screen.PassUpdateThrough);
        Assert.Equal(0, fired);

        // UpdateAvailable: modal -> blocks update-through, key fires the trigger.
        status.State = UpdateState.UpdateAvailable;
        stack.Update(0.016f, OverlayTestInput.KeyFrame(Key.U));
        Assert.False(screen.PassUpdateThrough);
        Assert.Equal(1, fired);
    }
}
