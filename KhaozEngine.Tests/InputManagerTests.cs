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

    private static RawInputState MouseInWindow(int x, int y, bool down, Rectangle windowBounds) =>
        new(new Point(x, y), down, false, false, 0,
            new KeyboardState(), NoPads, Array.Empty<TouchPoint>(), windowBounds);

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

    // --- in-window check is offset-agnostic (0.1.3 regression) ---

    [Fact]
    public void TapRegistersWhenWindowIsOffsetOnScreen()
    {
        // Desktop mouse coords are window-relative; WindowBounds.Location carries the window's
        // screen offset. The in-window check must ignore Location, else an offset window (the
        // normal case) suppresses every click.
        var im = new InputManager();
        var rect = new Rectangle(0, 0, 40, 40);
        var offsetWindow = new Rectangle(1060, 242, 440, 956);
        im.Update(MouseInWindow(20, 20, down: true, offsetWindow), true);
        im.Update(MouseInWindow(20, 20, down: false, offsetWindow), true);
        Assert.True(im.IsTapIn(rect));
    }

    [Fact]
    public void ClickOutsideClientAreaIsSuppressed()
    {
        // A position beyond the client width/height (mouse left the window) is still rejected.
        var im = new InputManager();
        var window = new Rectangle(1060, 242, 440, 956);
        im.Update(MouseInWindow(500, 50, down: true, window), true);   // x=500 >= width 440
        Assert.False(im.IsPointerDown);
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

    // --- helpers for 0.2.0 tests ---
    private static RawInputState MouseButtons(int x, int y, bool left, bool middle, bool right) =>
        new(new Point(x, y), left, middle, right, 0,
            new KeyboardState(), NoPads, Array.Empty<TouchPoint>(), Rectangle.Empty);

    private static RawInputState Touches2(Vector2 a, Vector2 b, int idA = 1, int idB = 2) =>
        new(Point.Zero, false, false, false, 0, new KeyboardState(), NoPads,
            new[] { new TouchPoint(a, TouchLocationState.Moved, idA), new TouchPoint(b, TouchLocationState.Moved, idB) },
            Rectangle.Empty);

    private static RawInputState Stick(float x, float y) =>
        new(Point.Zero, false, false, false, 0, new KeyboardState(),
            new[] { new GamePadState(new GamePadThumbSticks(new Vector2(x, y), Vector2.Zero),
                        new GamePadTriggers(), new GamePadButtons(), new GamePadDPad()),
                    new GamePadState(), new GamePadState(), new GamePadState() },
            Array.Empty<TouchPoint>(), Rectangle.Empty);

    // --- middle / right mouse edges ---

    [Fact]
    public void MiddleButtonEdges()
    {
        var im = new InputManager();
        im.Update(MouseButtons(0, 0, false, true, false), true);
        Assert.True(im.IsMiddleDown);
        Assert.True(im.IsMiddleJustPressed);
        im.Update(MouseButtons(0, 0, false, true, false), true);
        Assert.False(im.IsMiddleJustPressed);          // held
        im.Update(MouseButtons(0, 0, false, false, false), true);
        Assert.True(im.IsMiddleJustReleased);          // the "middle click"
        Assert.False(im.IsMiddleDown);
    }

    [Fact]
    public void RightButtonEdges()
    {
        var im = new InputManager();
        im.Update(MouseButtons(0, 0, false, false, true), true);
        Assert.True(im.IsRightDown);
        Assert.True(im.IsRightJustPressed);
        im.Update(MouseButtons(0, 0, false, false, false), true);
        Assert.True(im.IsRightJustReleased);
    }

    [Fact]
    public void MouseButtonsSuppressedWhenInactiveOrMobile()
    {
        var im = new InputManager();
        im.Update(MouseButtons(0, 0, false, true, true), false);   // window inactive
        Assert.False(im.IsMiddleDown);
        Assert.False(im.IsRightDown);

        var mob = new InputManager(isMobile: true);
        mob.Update(MouseButtons(0, 0, false, true, true), true);   // mobile has no mouse
        Assert.False(mob.IsMiddleDown);
        Assert.False(mob.IsRightDown);
    }

    // --- multi-touch surfacing ---

    [Fact]
    public void TouchesSurfacedInVirtualCoordinatesWithIds()
    {
        var t = new MatrixTransform(Matrix.CreateScale(0.5f));
        var im = new InputManager(isMobile: true, transform: t);
        im.Update(Touches2(new Vector2(100, 200), new Vector2(40, 60), idA: 7, idB: 9), true);

        Assert.Equal(2, im.Touches.Count);
        Assert.Equal(new Vector2(50, 100), im.Touches[0].Position);   // 100,200 * 0.5
        Assert.Equal(7, im.Touches[0].Id);
        Assert.Equal(9, im.Touches[1].Id);
    }

    [Fact]
    public void TouchesEmptyOnDesktop()
    {
        var im = new InputManager();
        im.Update(Mouse(10, 10, true), true);
        Assert.Empty(im.Touches);
    }

    // --- richer pinch ---

    [Fact]
    public void TryGetPinchReportsMidpointDistanceAndScale()
    {
        var im = new InputManager(isMobile: true);
        im.Update(Touches2(new Vector2(0, 0), new Vector2(10, 0)), true);   // distance 10, first frame
        Assert.True(im.TryGetPinch(out var p1));
        Assert.True(p1.Active);
        Assert.Equal(new Vector2(5, 0), p1.Midpoint);
        Assert.Equal(10f, p1.Distance);
        Assert.Equal(1f, p1.Scale);                                        // first frame ratio = 1

        im.Update(Touches2(new Vector2(0, 0), new Vector2(20, 0)), true);  // distance 20
        Assert.True(im.TryGetPinch(out var p2));
        Assert.Equal(20f, p2.Distance);
        Assert.Equal(10f, p2.Delta);                                       // 20 - 10
        Assert.Equal(2f, p2.Scale);                                        // 20 / 10
    }

    [Fact]
    public void TryGetPinchFalseWithFewerThanTwoTouches()
    {
        var im = new InputManager(isMobile: true);
        im.Update(Touch(new Vector2(5, 5), TouchLocationState.Moved), true);
        Assert.False(im.TryGetPinch(out var p));
        Assert.False(p.Active);
    }

    // --- controller cursor ---

    [Fact]
    public void CursorDriftsWithLeftStickWhenMouseIdle()
    {
        var im = new InputManager(cursorSpeed: 100f);     // identity transform, no clamp
        im.Update(Stick(0, 0), true, 0.016f);             // frame 1: snaps to mouse (0,0)
        im.Update(Stick(1, 0), true, 0.5f);               // mouse idle => drift +X by 1*100*0.5
        Assert.Equal(50f, im.PointerPosition.X, 3);
        Assert.Equal(0f, im.PointerPosition.Y, 3);
    }

    [Fact]
    public void CursorClampsToVirtualBounds()
    {
        var t = new MatrixTransform(Matrix.Identity, new Rectangle(0, 0, 10, 10));
        var im = new InputManager(transform: t, cursorSpeed: 1000f);
        im.Update(Stick(0, 0), true, 0.016f);             // snap to mouse (0,0)
        // stick down-right: x=+1, y=-1 (thumbstick up is +Y, which is screen -Y) => drift +X,+Y
        im.Update(Stick(1, -1), true, 1f);                // huge drift => clamp to (10,10)
        Assert.Equal(10f, im.PointerPosition.X, 3);
        Assert.Equal(10f, im.PointerPosition.Y, 3);
    }

    [Fact]
    public void MouseMovementSnapsCursorBack()
    {
        var im = new InputManager(cursorSpeed: 100f);
        im.Update(Stick(0, 0), true, 0.016f);
        im.Update(Stick(1, 0), true, 0.5f);               // drift to ~50
        im.Update(new RawInputState(new Point(200, 80), false, false, false, 0,
            new KeyboardState(), NoPads, Array.Empty<TouchPoint>(), Rectangle.Empty), true, 0.5f);
        Assert.Equal(new Vector2(200, 80), im.PointerPosition);
    }

    [Fact]
    public void CursorSpeedZeroIgnoresStick()
    {
        var im = new InputManager();                      // cursorSpeed defaults to 0
        im.Update(Stick(0, 0), true, 0.016f);
        im.Update(Stick(1, 0), true, 0.5f);               // stick must NOT move the pointer
        Assert.Equal(Vector2.Zero, im.PointerPosition);
    }
}
