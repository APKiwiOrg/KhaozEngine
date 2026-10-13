using System;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace GestureInputProbe;

internal sealed class ProbeScene
{
    const float HeaderHeight = 180;
    const float BodyHeight = 1200;
    readonly AppWindow _window;
    readonly Render2DSurface _surface;
    readonly Texture2D _white;
    readonly DpiFont _font;
    readonly UiViewport _viewport = new();
    readonly InputManager _input = new();
    readonly ProbeDiagnostics _diagnostics = new();
    readonly PannableCanvas _canvas = new()
    {
        ContentBounds = new(0, 0, 2000, 2000), MinZoom = .25f, MaxZoom = 8,
        Wheel = CanvasWheelMode.Pan, ScrollPanSpeed = 40
    };
    readonly DraggableBottomSheet _sheet = new(new[]
    {
        BottomSheetDetent.ContentCapped("peek", .45f), BottomSheetDetent.Fraction("full", 1)
    }, "peek", new() { HandleToggleDetentIds = new[] { "peek", "full" } });
    SpriteFont? _frameFont;
    bool _sheetFocus, _initialized;

    public ProbeScene(AppWindow window, Render2DSurface surface, Texture2D white, DpiFont font)
    {
        _window = window;
        _surface = surface;
        _white = white;
        _font = font;
        _sheet.WhiteTexture = white;
        _canvas.GestureOptions.KeyboardEnabled = true;
        _canvas.Camera.Position = new(1000, 1000);
    }

    public void Prepare(Frame frame)
    {
        _viewport.Update(frame);
        _input.Update(frame.Input, _viewport);
        _canvas.Viewport = new(0, HeaderHeight, _viewport.Width, MathF.Max(0, _viewport.Height - HeaderHeight));
        if (!_initialized && _canvas.Viewport.Height > 0)
        {
            _canvas.Focus(new(400, 400, 1200, 1200));
            _initialized = true;
        }
        if (frame.Input.WasPressed(Key.Escape)) _window.Close();
        if (frame.Input.WasPressed(Key.Tab)) _sheetFocus = !_sheetFocus;
        if (frame.Input.WasPressed(Key.C) || frame.Input.WasPressed(Key.R))
        {
            _canvas.CancelGestures();
            _sheet.CancelGestures();
            if (frame.Input.WasPressed(Key.R)) _canvas.CenterContent();
        }
        if (_sheetFocus && frame.Input.WasPressed(Key.Space)) _sheet.ToggleHandle();
        _input.BlockInputRegion(new(0, 0, _viewport.Width, HeaderHeight));
        _sheet.Update(_input, _canvas.Viewport, default, BodyHeight, frame.Dt);
        _canvas.Update(_input, frame.Dt, hasKeyboardFocus: !_sheetFocus);
        _diagnostics.Update(_window, frame, _input);
        if (!frame.RenderSuppressed) _frameFont = _font.For(frame.DpiScale);
    }

    public void Draw(Frame frame)
    {
        if (frame.RenderSuppressed || _frameFont is not { } font) return;
        _surface.NewFrame(frame);
        var batch = _surface.Batch;
        batch.Begin(_viewport);
        try
        {
            batch.SetScissor(_canvas.Viewport);
            try { DrawGrid(batch); }
            finally { batch.ClearScissor(); }
            _sheet.Draw(batch, (body, offset) => DrawBody(batch, font, body, offset));
            batch.Draw(_white, new Rect(0, 0, _viewport.Width, HeaderHeight), new Color(.08f, .10f, .14f));
            TextHelper.Draw(batch, font, "Tab: focus map/sheet | Space: sheet toggle | C: cancel | R: centre | Esc: close", 8, 4, Color.White);
            TextHelper.Draw(batch, font, _diagnostics.Summary, 8, 24, Color.White);
            TextHelper.Draw(batch, font, _diagnostics.Mapping, 8, 44, Color.White);
            TextHelper.Draw(batch, font, $"NativeGestureFailure={_diagnostics.Failure}", 8, 64, new Color(1, .7f, .4f));
            int row = 0;
            foreach (string line in _diagnostics.Recent)
                TextHelper.Draw(batch, font, line, 8, 84 + 18 * row++, Color.White);
            TextHelper.Draw(batch, font, $"focus={(_sheetFocus ? "sheet" : "map")} zoom={_canvas.Camera.Zoom:0.###} "
                + $"sheet={_sheet.CurrentDetentId}->{_sheet.TargetDetentId} scroll={_sheet.ScrollOffset:0.##}", 8, 158, Color.White);
        }
        finally { batch.End(); }
    }

    void DrawGrid(SpriteBatch batch)
    {
        var topLeft = _canvas.WorldToScreen(Vector2.Zero);
        var bottomRight = _canvas.WorldToScreen(new(2000, 2000));
        batch.Draw(_white, new Rect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X,
            bottomRight.Y - topLeft.Y), new Color(.12f, .20f, .24f));
        var line = new Color(.35f, .55f, .6f);
        for (int world = 0; world <= 2000; world += 50)
        {
            var p = _canvas.WorldToScreen(new(world, world));
            batch.Draw(_white, new Rect(p.X, topLeft.Y, 1, bottomRight.Y - topLeft.Y), line);
            batch.Draw(_white, new Rect(topLeft.X, p.Y, bottomRight.X - topLeft.X, 1), line);
        }
        var centre = _canvas.WorldToScreen(new(1000, 1000));
        batch.Draw(_white, new Rect(centre.X - 6, centre.Y - 6, 12, 12), new Color(1, .6f, .2f));
    }

    void DrawBody(SpriteBatch batch, SpriteFont font, Rect body, float offset)
    {
        for (int row = 0; row < 30; row++)
        {
            float y = body.Y + row * 40 - offset;
            batch.Draw(_white, new Rect(body.X + 8, y + 2, MathF.Max(0, body.Width - 16), 36),
                new Color(.16f + row % 2 * .04f, .20f, .26f));
            TextHelper.Draw(batch, font, $"Diagnostic body row {row + 1}: drag to expand, then scroll", body.X + 16, y + 10, Color.White);
        }
    }
}
