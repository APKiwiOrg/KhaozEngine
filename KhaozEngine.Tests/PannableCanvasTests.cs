using System;
using System.Collections.Generic;
using KhaozEngine.Input;
using KhaozEngine.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using Xunit;

namespace KhaozEngine.Tests;

public class PannableCanvasTests
{
    private static readonly IReadOnlyList<GamePadState> NoPads =
        new[] { new GamePadState(), new GamePadState(), new GamePadState(), new GamePadState() };

    private static RawInputState Mouse(int x, int y, bool down, int scroll = 0) =>
        new(new Point(x, y), down, false, false, scroll,
            new KeyboardState(), NoPads, Array.Empty<TouchPoint>(), Rectangle.Empty);

    private static RawInputState NoTouch() =>
        new(Point.Zero, false, false, false, 0,
            new KeyboardState(), NoPads, Array.Empty<TouchPoint>(), Rectangle.Empty);

    private static PannableCanvas MakeCanvas(InputManager im, Rectangle? content = null) =>
        new(im)
        {
            Viewport = new Rectangle(0, 0, 200, 200),
            ContentBounds = content ?? new Rectangle(-1000, -1000, 2000, 2000),
        };

    private const float Tol = 1e-2f;

    private static RawInputState Touches2(Vector2 a, Vector2 b) =>
        new(Point.Zero, false, false, false, 0, new KeyboardState(), NoPads,
            new[] { new TouchPoint(a, TouchLocationState.Moved, 1), new TouchPoint(b, TouchLocationState.Moved, 2) },
            Rectangle.Empty);

    [Fact]
    public void ScreenWorldRoundTrips()
    {
        var im = new InputManager();
        var canvas = MakeCanvas(im);
        canvas.CenterOn(new Vector2(37, -19));   // give the camera a non-zero offset

        foreach (var w in new[] { new Vector2(0, 0), new Vector2(50, 80), new Vector2(-123, 456) })
            Assert.Equal(w, canvas.ScreenToWorld(canvas.WorldToScreen(w)));
    }

    [Fact]
    public void DragInsideViewportAccumulatesOffset()
    {
        var im = new InputManager();
        var canvas = MakeCanvas(im);   // large content -> clamp does not interfere

        // Hover frame first so the press frame has zero pointer delta.
        im.Update(Mouse(50, 50, false), true);  canvas.Update();
        im.Update(Mouse(50, 50, true), true);   canvas.Update();   // press, delta 0
        im.Update(Mouse(70, 50, true), true);   canvas.Update();   // drag +20 x
        im.Update(Mouse(90, 50, true), true);   canvas.Update();   // drag +20 x

        Assert.Equal(new Vector2(40, 0), canvas.CameraOffset);
    }

    [Fact]
    public void DragBeganOutsideViewportDoesNotPan()
    {
        var im = new InputManager();
        var canvas = MakeCanvas(im);

        im.Update(Mouse(300, 300, false), true); canvas.Update();
        im.Update(Mouse(300, 300, true), true);  canvas.Update();   // press OUTSIDE viewport
        im.Update(Mouse(120, 140, true), true);  canvas.Update();   // move inside while down

        Assert.Equal(Vector2.Zero, canvas.CameraOffset);
    }

    [Fact]
    public void ClampKeepsCameraWithinBounds()
    {
        var im = new InputManager();
        var canvas = MakeCanvas(im, new Rectangle(0, 0, 400, 400));   // content 400, viewport 200

        im.Update(Mouse(100, 100, false), true); canvas.Update();
        im.Update(Mouse(100, 100, true), true);  canvas.Update();
        im.Update(Mouse(5000, 100, true), true); canvas.Update();     // huge drag right

        // maxOffX = -Left - halfW = 0 - 100 = -100
        Assert.Equal(-100f, canvas.CameraOffset.X);
    }

    [Fact]
    public void ClampCentersAxisWhenContentSmallerThanViewport()
    {
        var im = new InputManager();
        var canvas = MakeCanvas(im, new Rectangle(0, 0, 100, 100));   // content smaller than viewport

        im.Update(Mouse(100, 100, false), true);   canvas.Update();
        im.Update(Mouse(100, 100, true), true);    canvas.Update();
        im.Update(Mouse(5000, 5000, true), true);  canvas.Update();   // drag far in both axes

        // centered: -(X + W/2) = -(0 + 50) = -50 on each axis
        Assert.Equal(new Vector2(-50, -50), canvas.CameraOffset);
    }

    [Fact]
    public void UpdateBlocksViewportRegionWhenBlockInputTrue()
    {
        var im = new InputManager();
        var canvas = MakeCanvas(im);

        im.Update(NoTouch(), true);
        canvas.Update();
        Assert.True(im.IsInputBlocked(new Vector2(100, 100)));
        Assert.False(im.IsInputBlocked(new Vector2(500, 500)));

        canvas.BlockInput = false;
        im.Update(NoTouch(), true);   // clears the previous frame's blocked region
        canvas.Update();
        Assert.False(im.IsInputBlocked(new Vector2(100, 100)));
    }

    [Fact]
    public void WheelScrollPansVerticallyByScrollPanSpeed()
    {
        var im = new InputManager();
        var canvas = MakeCanvas(im);   // large content, pointer inside viewport

        im.Update(Mouse(50, 50, false, scroll: 0), true);    canvas.Update();   // baseline wheel value
        im.Update(Mouse(50, 50, false, scroll: 120), true);  canvas.Update();   // delta 120

        Assert.Equal(60f, canvas.CameraOffset.Y);   // 120 * 0.5
        Assert.Equal(0f, canvas.CameraOffset.X);
    }

    [Fact]
    public void WheelScrollIgnoredWhenPointerOutsideViewport()
    {
        var im = new InputManager();
        var canvas = MakeCanvas(im);

        im.Update(Mouse(500, 500, false, scroll: 0), true);   canvas.Update();
        im.Update(Mouse(500, 500, false, scroll: 120), true); canvas.Update();   // pointer outside

        Assert.Equal(0f, canvas.CameraOffset.Y);
    }

    [Fact]
    public void TryGetTapMapsPressAndReleaseToWorld()
    {
        var im = new InputManager();
        var canvas = MakeCanvas(im);   // offset stays 0 (no drag/scroll)

        im.Update(Mouse(120, 140, false), true); canvas.Update();
        im.Update(Mouse(120, 140, true), true);  canvas.Update();
        im.Update(Mouse(120, 140, false), true); canvas.Update();   // release -> tap

        Assert.True(canvas.TryGetTap(out var press, out var release));
        Assert.Equal(new Vector2(20, 40), press);     // screen 120,140 minus viewport center 100,100
        Assert.Equal(new Vector2(20, 40), release);
    }

    [Fact]
    public void TryGetTapFalseWhenPressBeganOutsideViewport()
    {
        var im = new InputManager();
        var canvas = MakeCanvas(im);

        im.Update(Mouse(300, 300, false), true); canvas.Update();
        im.Update(Mouse(300, 300, true), true);  canvas.Update();   // press outside
        im.Update(Mouse(120, 140, false), true); canvas.Update();   // release inside

        Assert.False(canvas.TryGetTap(out _, out _));
    }

    [Fact]
    public void TryGetTapFalseWhenNotReleasedThisFrame()
    {
        var im = new InputManager();
        var canvas = MakeCanvas(im);

        im.Update(Mouse(120, 140, false), true); canvas.Update();
        im.Update(Mouse(120, 140, true), true);  canvas.Update();   // still pressed

        Assert.False(canvas.TryGetTap(out _, out _));
    }

    [Fact]
    public void PointerWorldMapsCurrentPointer()
    {
        var im = new InputManager();
        var canvas = MakeCanvas(im);

        im.Update(Mouse(130, 160, false), true); canvas.Update();

        Assert.Equal(new Vector2(30, 60), canvas.PointerWorld);   // 130,160 minus center 100,100
    }

    [Fact]
    public void CenterOnPlacesPointAtViewportCenter()
    {
        var im = new InputManager();
        var canvas = MakeCanvas(im);   // large content -> no clamp

        canvas.CenterOn(new Vector2(50, 60));

        Assert.Equal(new Vector2(100, 100), canvas.WorldToScreen(new Vector2(50, 60)));
    }

    [Fact]
    public void FocusCentersOnRectCenter()
    {
        var im = new InputManager();
        var canvas = MakeCanvas(im);

        canvas.Focus(new Rectangle(20, 20, 40, 40));   // center (40,40)

        Assert.Equal(new Vector2(100, 100), canvas.WorldToScreen(new Vector2(40, 40)));
    }

    [Fact]
    public void CenterContentCentersOnContentMiddle()
    {
        var im = new InputManager();
        var canvas = MakeCanvas(im, new Rectangle(0, 0, 400, 400));

        canvas.CenterContent();

        Assert.Equal(new Vector2(-200, -200), canvas.CameraOffset);   // -(content center 200,200)
        Assert.Equal(new Vector2(100, 100), canvas.WorldToScreen(new Vector2(200, 200)));
    }

    [Fact]
    public void PinchZoomsAboutMidpoint()
    {
        var im = new InputManager(isMobile: true);
        var canvas = MakeCanvas(im);
        canvas.MaxZoom = 100f;

        im.Update(Touches2(new Vector2(60, 100), new Vector2(140, 100)), true); canvas.Update();  // mid 100, dist 80
        im.Update(Touches2(new Vector2(20, 100), new Vector2(180, 100)), true); canvas.Update();  // mid 100, dist 160 -> 2x

        Assert.Equal(2f, canvas.Camera.Zoom, Tol);
    }

    [Fact]
    public void PinchTwoFingerDragPans()
    {
        var im = new InputManager(isMobile: true);
        var canvas = MakeCanvas(im);   // large content -> no clamp interference

        im.Update(Touches2(new Vector2(60, 100), new Vector2(140, 100)), true); canvas.Update();  // mid 100, dist 80
        im.Update(Touches2(new Vector2(90, 100), new Vector2(170, 100)), true); canvas.Update();  // mid 130, dist 80

        Assert.Equal(1f, canvas.Camera.Zoom, Tol);              // distance unchanged -> no zoom
        Assert.True(Math.Abs(canvas.CameraOffset.X - 30f) < 0.01f, $"offset.X was {canvas.CameraOffset.X}");  // mid +30, zoom 1
    }

    [Fact]
    public void PinchDoesNotZoomWhenZoomDisabled()
    {
        var im = new InputManager(isMobile: true);
        var canvas = MakeCanvas(im);
        canvas.EnableZoom = false;

        im.Update(Touches2(new Vector2(60, 100), new Vector2(140, 100)), true); canvas.Update();
        im.Update(Touches2(new Vector2(20, 100), new Vector2(180, 100)), true); canvas.Update();  // spread

        Assert.Equal(1f, canvas.Camera.Zoom, Tol);
    }

    [Fact]
    public void TransformsStayZoomCorrectAfterPinch()
    {
        var im = new InputManager(isMobile: true);
        var canvas = MakeCanvas(im);
        canvas.MaxZoom = 100f;

        im.Update(Touches2(new Vector2(60, 100), new Vector2(140, 100)), true); canvas.Update();
        im.Update(Touches2(new Vector2(20, 100), new Vector2(180, 100)), true); canvas.Update();  // ~2x

        // Round-trip must still hold under non-unit zoom.
        foreach (var w in new[] { new Vector2(0, 0), new Vector2(33, -41) })
            Assert.True(Vector2.Distance(w, canvas.ScreenToWorld(canvas.WorldToScreen(w))) < 0.01f);
    }
}
