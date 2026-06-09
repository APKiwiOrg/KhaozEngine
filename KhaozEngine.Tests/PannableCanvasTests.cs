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
}
