using System;
using System.Collections.Generic;
using KhaozEngine.Graphics;
using KhaozEngine.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using Xunit;

namespace KhaozEngine.Tests;

public class CameraControllerTests
{
    private const float Tol = 1e-2f;
    private static Viewport Vp => new Viewport(0, 0, 800, 600);   // center (400, 300)

    private static readonly IReadOnlyList<GamePadState> NoPads =
        new[] { new GamePadState(), new GamePadState(), new GamePadState(), new GamePadState() };

    private static RawInputState Mouse(int x, int y, bool down, int scroll = 0) =>
        new(new Point(x, y), down, false, false, scroll,
            new KeyboardState(), NoPads, Array.Empty<TouchPoint>(), Rectangle.Empty);

    private static RawInputState Touches2(Vector2 a, Vector2 b) =>
        new(Point.Zero, false, false, false, 0, new KeyboardState(), NoPads,
            new[] { new TouchPoint(a, TouchLocationState.Moved, 1), new TouchPoint(b, TouchLocationState.Moved, 2) },
            Rectangle.Empty);

    // World bounds large enough that ClampPosition never moves the camera off (0,0).
    private static readonly Rectangle Unbounded = new(-1_000_000, -1_000_000, 2_000_000, 2_000_000);

    private static void AssertClose(Vector2 expected, Vector2 actual, float tol = Tol) =>
        Assert.True(Vector2.Distance(expected, actual) <= tol, $"expected {expected}, got {actual}");

    [Fact]
    public void DragPansCameraOppositeInWorldSpace()
    {
        var cam = new Camera2D();
        var im = new InputManager();
        var ctrl = new CameraController(im, cam);

        im.Update(Mouse(50, 50, false), true); ctrl.Update(Vp, Unbounded);   // hover
        im.Update(Mouse(50, 50, true), true);  ctrl.Update(Vp, Unbounded);   // press, delta 0
        im.Update(Mouse(70, 50, true), true);  ctrl.Update(Vp, Unbounded);   // drag +20
        im.Update(Mouse(90, 50, true), true);  ctrl.Update(Vp, Unbounded);   // drag +20

        // Grab-and-drag: content follows the finger, so the camera moves the opposite way.
        AssertClose(new Vector2(-40, 0), cam.Position);
    }

    [Fact]
    public void PanAccountsForZoom()
    {
        var cam = new Camera2D { Zoom = 2f };
        var im = new InputManager();
        var ctrl = new CameraController(im, cam);

        im.Update(Mouse(50, 50, false), true); ctrl.Update(Vp, Unbounded);
        im.Update(Mouse(50, 50, true), true);  ctrl.Update(Vp, Unbounded);
        im.Update(Mouse(90, 50, true), true);  ctrl.Update(Vp, Unbounded);   // drag +40 screen

        // world delta = screen delta / zoom = 40 / 2 = 20.
        AssertClose(new Vector2(-20, 0), cam.Position);
    }

    [Fact]
    public void TwoFingerDragPansByMidpoint()
    {
        var cam = new Camera2D();
        var im = new InputManager(isMobile: true);
        var ctrl = new CameraController(im, cam) { MaxZoom = 100f };

        im.Update(Touches2(new Vector2(300, 300), new Vector2(400, 300)), true); ctrl.Update(Vp, Unbounded); // mid 350, dist 100
        im.Update(Touches2(new Vector2(330, 300), new Vector2(430, 300)), true); ctrl.Update(Vp, Unbounded); // mid 380, dist 100

        // Distance unchanged (no zoom); midpoint moved +30 -> camera pans -30 in world (zoom 1).
        Assert.Equal(1f, cam.Zoom, Tol);
        AssertClose(new Vector2(-30, 0), cam.Position);
    }

    [Fact]
    public void PinchZoomsByDistanceRatio()
    {
        var cam = new Camera2D();
        var im = new InputManager(isMobile: true);
        var ctrl = new CameraController(im, cam) { MaxZoom = 100f };

        // Symmetric spread about a fixed midpoint (350): pure zoom, no pan.
        im.Update(Touches2(new Vector2(300, 300), new Vector2(400, 300)), true); ctrl.Update(Vp, Unbounded); // dist 100
        im.Update(Touches2(new Vector2(250, 300), new Vector2(450, 300)), true); ctrl.Update(Vp, Unbounded); // dist 200 -> 2x

        Assert.Equal(2f, cam.Zoom, Tol);
    }

    [Fact]
    public void WheelZoomClampsToMaxZoom()
    {
        var cam = new Camera2D();
        var im = new InputManager();
        var ctrl = new CameraController(im, cam) { MaxZoom = 3f, WheelZoomStep = 2f };

        im.Update(Mouse(400, 300, false, scroll: 0), true);    ctrl.Update(Vp, Unbounded);   // baseline
        im.Update(Mouse(400, 300, false, scroll: 1200), true); ctrl.Update(Vp, Unbounded);   // +10 notches -> 2^10

        Assert.Equal(3f, cam.Zoom, Tol);
    }

    [Fact]
    public void WheelZoomClampsToMinZoom()
    {
        var cam = new Camera2D();
        var im = new InputManager();
        var ctrl = new CameraController(im, cam) { MinZoom = 0.5f, WheelZoomStep = 2f };

        im.Update(Mouse(400, 300, false, scroll: 0), true);     ctrl.Update(Vp, Unbounded);
        im.Update(Mouse(400, 300, false, scroll: -1200), true); ctrl.Update(Vp, Unbounded);  // zoom way out

        Assert.Equal(0.5f, cam.Zoom, Tol);
    }

    [Fact]
    public void WheelZoomKeepsFocalPointUnderCursor()
    {
        var cam = new Camera2D();
        var im = new InputManager();
        var ctrl = new CameraController(im, cam) { WheelZoomStep = 1.5f };

        // Cursor at (500,300): world (100,0) under it before zoom.
        var worldBefore = cam.ScreenToWorld(new Vector2(500, 300), Vp);
        AssertClose(new Vector2(100, 0), worldBefore);

        im.Update(Mouse(500, 300, false, scroll: 0), true);   ctrl.Update(Vp, Unbounded);
        im.Update(Mouse(500, 300, false, scroll: 120), true); ctrl.Update(Vp, Unbounded);   // zoom in one notch

        Assert.True(cam.Zoom > 1f);
        AssertClose(worldBefore, cam.ScreenToWorld(new Vector2(500, 300), Vp));   // focus pinned
    }

    [Fact]
    public void PanClampsToWorldBounds()
    {
        var cam = new Camera2D { Position = new Vector2(500, 500), Zoom = 1f };
        var im = new InputManager();
        var ctrl = new CameraController(im, cam);
        var bounds = new Rectangle(0, 0, 1000, 1000);   // halfW 400 -> X[400,600], halfH 300 -> Y[300,700]

        im.Update(Mouse(400, 300, false), true);  ctrl.Update(Vp, bounds);
        im.Update(Mouse(400, 300, true), true);   ctrl.Update(Vp, bounds);
        im.Update(Mouse(9000, 9000, true), true); ctrl.Update(Vp, bounds);   // huge drag down-right -> camera up-left

        AssertClose(new Vector2(400, 300), cam.Position);   // clamped to the top-left edges
    }

    [Fact]
    public void TapReturnsPressAndReleaseWorld()
    {
        var cam = new Camera2D();
        var im = new InputManager();
        var ctrl = new CameraController(im, cam);

        im.Update(Mouse(450, 320, false), true); ctrl.Update(Vp, Unbounded);
        im.Update(Mouse(450, 320, true), true);  ctrl.Update(Vp, Unbounded);
        im.Update(Mouse(450, 320, false), true); ctrl.Update(Vp, Unbounded);   // release -> tap

        Assert.True(ctrl.TryGetTap(out var press, out var release));
        AssertClose(new Vector2(50, 20), press);     // screen 450,320 minus center 400,300
        AssertClose(new Vector2(50, 20), release);
    }

    [Fact]
    public void DragYieldsDifferentPressAndReleaseWorld()
    {
        var cam = new Camera2D();
        var im = new InputManager();
        var ctrl = new CameraController(im, cam);

        im.Update(Mouse(450, 320, false), true); ctrl.Update(Vp, Unbounded);
        im.Update(Mouse(450, 320, true), true);  ctrl.Update(Vp, Unbounded);   // press
        im.Update(Mouse(550, 320, true), true);  ctrl.Update(Vp, Unbounded);   // drag (camera moved)
        im.Update(Mouse(550, 320, false), true); ctrl.Update(Vp, Unbounded);   // release inside

        Assert.True(ctrl.TryGetTap(out var press, out var release));
        // A pan ends "inside" too, but the camera moved between press and release, so the world
        // points differ -- the caller's same-target check rejects it as a tap.
        Assert.True(Vector2.Distance(press, release) > 1f);
    }

    [Fact]
    public void TapFalseWhenPressBeganOutsideViewport()
    {
        var cam = new Camera2D();
        var im = new InputManager();
        var ctrl = new CameraController(im, cam);
        var small = new Viewport(0, 0, 200, 200);

        im.Update(Mouse(500, 500, false), true); ctrl.Update(small, Unbounded);
        im.Update(Mouse(500, 500, true), true);  ctrl.Update(small, Unbounded);   // press outside
        im.Update(Mouse(100, 100, false), true); ctrl.Update(small, Unbounded);   // release inside

        Assert.False(ctrl.TryGetTap(out _, out _));
    }
}
