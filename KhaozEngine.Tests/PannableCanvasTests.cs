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
}
