using System;
using System.Collections.Generic;
using KhaozEngine.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using Xunit;

namespace KhaozEngine.Tests;

public class InputManagerTests
{
    private static readonly IReadOnlyList<GamePadState> NoPads =
        new[] { new GamePadState(), new GamePadState(), new GamePadState(), new GamePadState() };

    private static RawInputState Mouse(int x, int y, bool down, int scroll = 0, params Keys[] keys) =>
        new(new Point(x, y), down, false, false, scroll,
            new KeyboardState(keys), NoPads, Array.Empty<TouchPoint>(), Rectangle.Empty);

    private static RawInputState Pads(IReadOnlyList<GamePadState> pads) =>
        new(Point.Zero, false, false, false, 0,
            new KeyboardState(), pads, Array.Empty<TouchPoint>(), Rectangle.Empty);

    private static RawInputState Touch(Vector2 pos, TouchLocationState state) =>
        new(Point.Zero, false, false, false, 0,
            new KeyboardState(), NoPads, new[] { new TouchPoint(pos, state) }, Rectangle.Empty);

    private static RawInputState NoTouch() =>
        new(Point.Zero, false, false, false, 0,
            new KeyboardState(), NoPads, Array.Empty<TouchPoint>(), Rectangle.Empty);

    // --- pointer edges + tap invariant (Hardpoint) ---

    [Fact]
    public void PointerEdgesFireOnlyOnTransition()
    {
        var im = new InputManager();
        im.Update(Mouse(10, 10, true), true);
        Assert.True(im.IsPointerJustPressed);
        Assert.True(im.IsPointerDown);
        im.Update(Mouse(10, 10, true), true);
        Assert.False(im.IsPointerJustPressed);
        im.Update(Mouse(10, 10, false), true);
        Assert.True(im.IsPointerJustReleased);
        Assert.False(im.IsPointerDown);
    }

    [Fact]
    public void IsTapInRequiresPressOriginAndReleaseInside()
    {
        var im = new InputManager();
        var rect = new Rectangle(0, 0, 40, 40);
        im.Update(Mouse(20, 20, true), true);
        Assert.False(im.IsTapIn(rect));
        im.Update(Mouse(20, 20, false), true);
        Assert.True(im.IsTapIn(rect));
    }

    [Fact]
    public void IsTapInFalseWhenPressBeganOutside()
    {
        var im = new InputManager();
        var rect = new Rectangle(0, 0, 40, 40);
        im.Update(Mouse(100, 100, true), true);
        im.Update(Mouse(20, 20, false), true);
        Assert.False(im.IsTapIn(rect));
    }

    [Fact]
    public void InactiveWindowSuppressesPointer()
    {
        var im = new InputManager();
        im.Update(Mouse(10, 10, true), false);
        Assert.False(im.IsPointerDown);
        Assert.False(im.IsPointerJustPressed);
    }

    // --- region blocking (click-through fix, both games) ---

    [Fact]
    public void BlockedRegionsReportThisFrameAndClearNext()
    {
        var im = new InputManager();
        im.Update(NoTouch(), true);
        im.BlockInputRegion(new Rectangle(0, 0, 40, 40));
        Assert.True(im.IsInputBlocked(new Vector2(20, 20)));
        Assert.False(im.IsInputBlocked(new Vector2(100, 100)));
        im.Update(NoTouch(), true);
        Assert.False(im.IsInputBlocked(new Vector2(20, 20)));
    }

    // --- gestures (Nullwake) ---

    [Fact]
    public void DragDeltaOnlyWhenPressBeganInBounds()
    {
        var im = new InputManager();
        var rect = new Rectangle(0, 0, 100, 100);
        im.Update(Mouse(10, 10, true), true);          // press inside
        im.Update(Mouse(30, 10, true), true);          // drag right 20
        Assert.True(im.IsDraggingIn(rect));
        Assert.Equal(new Vector2(20, 0), im.GetDragDelta(rect));

        var im2 = new InputManager();
        im2.Update(Mouse(200, 200, true), true);       // press OUTSIDE
        im2.Update(Mouse(220, 200, true), true);
        Assert.False(im2.IsDraggingIn(rect));
        Assert.Equal(Vector2.Zero, im2.GetDragDelta(rect));
    }

    [Fact]
    public void ScrollDeltaOnlyWhenPointerInBounds()
    {
        var im = new InputManager();
        var rect = new Rectangle(0, 0, 100, 100);
        im.Update(Mouse(50, 50, false, scroll: 0), true);
        im.Update(Mouse(50, 50, false, scroll: 120), true);   // pointer inside
        Assert.Equal(120, im.GetScrollIn(rect));

        im.Update(Mouse(500, 500, false, scroll: 120), true);
        im.Update(Mouse(500, 500, false, scroll: 240), true); // pointer outside
        Assert.Equal(0, im.GetScrollIn(rect));
    }

    [Fact]
    public void IsReleasedOutsideFiresOnReleaseBeyondBounds()
    {
        var im = new InputManager();
        var rect = new Rectangle(0, 0, 40, 40);
        im.Update(Mouse(20, 20, true), true);
        im.Update(Mouse(200, 200, false), true);
        Assert.True(im.IsReleasedOutside(rect));
    }

    // --- keyboard + gamepad + menu nav (SpaceGame) ---

    [Fact]
    public void KeyboardEdgeDetection()
    {
        var im = new InputManager();
        im.Update(Mouse(0, 0, false, 0, Keys.Escape), true);
        Assert.True(im.IsKeyJustPressed(Keys.Escape));
        Assert.True(im.IsKeyDown(Keys.Escape));
        im.Update(Mouse(0, 0, false, 0, Keys.Escape), true);
        Assert.False(im.IsKeyJustPressed(Keys.Escape));
    }

    [Fact]
    public void MenuSelectFromKeyboard()
    {
        var im = new InputManager();
        im.Update(NoTouch(), true);                       // baseline
        im.Update(Mouse(0, 0, false, 0, Keys.Enter), true);
        Assert.True(im.IsMenuSelect(null, out _));
    }

    [Fact]
    public void MenuCancelFromKeyboard()
    {
        var im = new InputManager();
        im.Update(NoTouch(), true);
        im.Update(Mouse(0, 0, false, 0, Keys.Escape), true);
        Assert.True(im.IsMenuCancel(null, out _));
    }

    [Fact]
    public void NewButtonPressFromGamepadAnyPlayer()
    {
        var im = new InputManager();
        var pressed = new[]
        {
            new GamePadState(), new GamePadState(),
            new GamePadState(new GamePadThumbSticks(), new GamePadTriggers(),
                new GamePadButtons(Buttons.A), new GamePadDPad()),
            new GamePadState(),
        };
        im.Update(Pads(NoPads), true);                    // baseline: A up
        im.Update(Pads(pressed), true);                   // player 3 presses A
        Assert.True(im.IsNewButtonPress(Buttons.A, null, out PlayerIndex who));
        Assert.Equal(PlayerIndex.Three, who);
    }

    [Fact]
    public void PauseGameFromTapInRect()
    {
        var im = new InputManager();
        var rect = new Rectangle(0, 0, 40, 40);
        im.Update(Mouse(20, 20, true), true);             // press in rect
        im.Update(Mouse(20, 20, false), true);            // release in rect
        Assert.True(im.IsPauseGame(null, rect));
    }

    // --- coordinate transform seam ---

    [Fact]
    public void PointerRoutedThroughTransformAndClamped()
    {
        var transform = new MatrixTransform(Matrix.CreateScale(0.5f), new Rectangle(0, 0, 10, 10));
        var im = new InputManager(isMobile: false, transform: transform);
        im.Update(Mouse(40, 40, false), true);            // *0.5 = (20,20) -> clamped to (10,10)
        Assert.Equal(new Vector2(10, 10), im.PointerPosition);
    }
}
