using System.Numerics;
using KhaozEngine.Automation;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// The compose half of the input seam: injected state merged into a real snapshot. The union is the contract, so a
/// developer's own keyboard keeps working while automation clicks.
/// </summary>
public class AutomationComposeTests
{
    [Fact]
    public void InjectedPointerOverridesPositionAndInjectedHoldsUnionWithTheReal()
    {
        var injector = new AutomationInputInjector();
        injector.SetPointer(new Vector2(400, 300));
        injector.PressKey(Key.W, frame: 1, holdFrames: 0);
        injector.PressButton(MouseButton.Left, frame: 1, holdFrames: 0);

        InputState real = AutomationTestKit.Real(
            position: new Vector2(10, 10), keysDown: new[] { Key.LeftShift }, mouseDown: new[] { MouseButton.Right });
        InputState composed = injector.Compose(real);

        Assert.Equal(new Vector2(400, 300), composed.MousePosition);
        Assert.True(composed.IsDown(Key.W));
        Assert.True(composed.IsDown(Key.LeftShift));
        Assert.True(composed.WasPressed(Key.W));
        Assert.False(composed.WasPressed(Key.LeftShift));
        Assert.Contains(MouseButton.Left, composed.MouseDown);
        Assert.Contains(MouseButton.Right, composed.MouseDown);
        Assert.Contains(MouseButton.Left, composed.MousePressed);
    }

    [Fact]
    public void APressAndAReleaseInsideOneFrameKeepBothEdges()
    {
        var injector = new AutomationInputInjector();

        injector.PressButton(MouseButton.Left, frame: 1, holdFrames: 0);
        injector.ReleaseButton(MouseButton.Left);
        injector.PressKey(Key.E, frame: 1, holdFrames: 0);
        injector.ReleaseKey(Key.E);

        InputState composed = injector.Compose(AutomationTestKit.Real());

        Assert.Contains(MouseButton.Left, composed.MousePressed);
        Assert.Contains(MouseButton.Left, composed.MouseReleased);
        Assert.DoesNotContain(MouseButton.Left, composed.MouseDown);
        Assert.True(composed.WasPressed(Key.E));
        Assert.True(composed.WasReleased(Key.E));
        Assert.False(composed.IsDown(Key.E));
    }

    [Fact]
    public void WindowFocusedIsForcedTrueEvenWhenTheOsSaysOtherwise()
    {
        // GuiSurface refuses hover and press while unfocused, so without this every injected click is dropped the
        // moment the agent's terminal takes focus.
        var injector = new AutomationInputInjector();

        InputState composed = injector.Compose(AutomationTestKit.Real(windowFocused: false));

        Assert.True(composed.WindowFocused);
    }

    [Fact]
    public void TheRealCursorKeepsThePointerUntilOneIsInjected()
    {
        var injector = new AutomationInputInjector();
        InputState real = AutomationTestKit.Real(position: new Vector2(7, 9));

        Assert.Equal(new Vector2(7, 9), injector.Compose(real).MousePosition);

        injector.SetPointer(new Vector2(100, 100));
        Assert.Equal(new Vector2(100, 100), injector.Compose(real).MousePosition);

        injector.ReleasePointer();
        Assert.Equal(new Vector2(7, 9), injector.Compose(real).MousePosition);
    }

    [Fact]
    public void AnInjectedPointerReportsItsOwnMotionAsTheDelta()
    {
        var injector = new AutomationInputInjector();
        InputState real = AutomationTestKit.Real(position: new Vector2(7, 9));

        injector.SetPointer(new Vector2(100, 100));
        injector.Compose(real);
        injector.SetPointer(new Vector2(130, 90));

        Assert.Equal(new Vector2(30, -10), injector.Compose(real).MouseDelta);
    }

    [Fact]
    public void EverythingElseOnTheSnapshotComesStraightFromTheRealFrame()
    {
        var injector = new AutomationInputInjector();
        InputState real = AutomationTestKit.Real(width: 1920, height: 1080);

        InputState composed = injector.Compose(real);

        Assert.Equal(1920, composed.Width);
        Assert.Equal(1080, composed.Height);
        Assert.Equal(real.ScrollDelta, composed.ScrollDelta);
        Assert.Same(real.KeysRepeated, composed.KeysRepeated);
        Assert.Same(real.Gamepads, composed.Gamepads);
        Assert.Same(real.Touches, composed.Touches);
    }

    [Fact]
    public void AnEdgeLastsExactlyOneFrame()
    {
        var injector = new AutomationInputInjector();
        InputState real = AutomationTestKit.Real();
        injector.PressKey(Key.Space, frame: 1, holdFrames: 0);

        Assert.True(injector.Compose(real).WasPressed(Key.Space));
        injector.EndFrame();

        InputState next = injector.Compose(real);
        Assert.False(next.WasPressed(Key.Space));
        Assert.True(next.IsDown(Key.Space));
    }

    [Fact]
    public void AHoldReleasesOnThePressFramePlusHoldFrames()
    {
        var injector = new AutomationInputInjector();
        InputState real = AutomationTestKit.Real();
        injector.PressButton(MouseButton.Left, frame: 10, holdFrames: 2);

        // Frames 10 and 11 hold it, frame 12 carries the release edge.
        AssertFrame(injector, real, frame: 10, down: true, released: false);
        AssertFrame(injector, real, frame: 11, down: true, released: false);
        AssertFrame(injector, real, frame: 12, down: false, released: true);
        AssertFrame(injector, real, frame: 13, down: false, released: false);
    }

    [Fact]
    public void AnExplicitReleaseCancelsThePendingAutoRelease()
    {
        var injector = new AutomationInputInjector();
        InputState real = AutomationTestKit.Real();
        injector.PressKey(Key.A, frame: 1, holdFrames: 5);
        injector.ReleaseKey(Key.A);

        Assert.True(injector.Compose(real).WasReleased(Key.A));
        injector.EndFrame();

        // The scheduled expiry must not fire a second release on frame 6.
        for (long frame = 2; frame <= 8; frame++)
        {
            injector.ExpireHolds(frame);
            Assert.False(injector.Compose(real).WasReleased(Key.A));
            injector.EndFrame();
        }
    }

    static void AssertFrame(AutomationInputInjector injector, InputState real, long frame, bool down, bool released)
    {
        injector.ExpireHolds(frame);
        InputState composed = injector.Compose(real);
        Assert.Equal(down, composed.MouseDown.Contains(MouseButton.Left));
        Assert.Equal(released, composed.WasReleased(MouseButton.Left));
        injector.EndFrame();
    }
}
