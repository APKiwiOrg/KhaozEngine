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

public class CameraGesturesTests
{
    private const float Tol = 1e-2f;
    private static Viewport Vp => new Viewport(0, 0, 800, 600);   // center (400, 300)

    private static readonly IReadOnlyList<GamePadState> NoPads =
        new[] { new GamePadState(), new GamePadState(), new GamePadState(), new GamePadState() };

    private static RawInputState Mouse(int x, int y, bool down) =>
        new(new Point(x, y), down, false, false, 0,
            new KeyboardState(), NoPads, Array.Empty<TouchPoint>(), Rectangle.Empty);

    private static void AssertClose(Vector2 expected, Vector2 actual, float tol = Tol) =>
        Assert.True(Vector2.Distance(expected, actual) <= tol, $"expected {expected}, got {actual}");

    [Fact]
    public void PinchTracker_FirstFrame_StoresMidpoint_NoPanNoZoom()
    {
        var cam = new Camera2D { Viewport = Vp };
        var tracker = new PinchGestureTracker();
        tracker.Apply(cam, new Pinch(true, new Vector2(400, 300), 100f, 0f, 1f), Vp, true, true, 0.1f, 10f);
        AssertClose(Vector2.Zero, cam.Position);
        Assert.Equal(1f, cam.Zoom, 3);
    }

    [Fact]
    public void PinchTracker_SecondFrame_PansByMidpointTravel()
    {
        var cam = new Camera2D { Viewport = Vp };
        var tracker = new PinchGestureTracker();
        tracker.Apply(cam, new Pinch(true, new Vector2(400, 300), 100f, 0f, 1f), Vp, true, true, 0.1f, 100f);
        tracker.Apply(cam, new Pinch(true, new Vector2(430, 300), 100f, 0f, 1f), Vp, true, true, 0.1f, 100f);
        AssertClose(new Vector2(-30, 0), cam.Position);   // midpoint +30, zoom 1 -> pan -30
    }

    [Fact]
    public void PinchTracker_Reset_ClearsContinuity()
    {
        var cam = new Camera2D { Viewport = Vp };
        var tracker = new PinchGestureTracker();
        tracker.Apply(cam, new Pinch(true, new Vector2(400, 300), 100f, 0f, 1f), Vp, true, true, 0.1f, 100f);
        tracker.Reset();
        tracker.Apply(cam, new Pinch(true, new Vector2(430, 300), 100f, 0f, 1f), Vp, true, true, 0.1f, 100f);
        AssertClose(Vector2.Zero, cam.Position);   // reset -> treated as first frame -> no pan
    }

    [Fact]
    public void CameraGestures_TryGetTap_MapsPressAndRelease()
    {
        var cam = new Camera2D();
        var im = new InputManager();
        im.Update(Mouse(450, 320, false), true);
        im.Update(Mouse(450, 320, true), true);
        im.Update(Mouse(450, 320, false), true);   // release -> tap
        Assert.True(CameraGestures.TryGetTap(im, cam, Vp, out var press, out var release));
        AssertClose(new Vector2(50, 20), press);    // 450,320 minus center 400,300
        AssertClose(new Vector2(50, 20), release);
    }
}
