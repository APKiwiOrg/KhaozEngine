using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui;

/// <summary>The tooltip's ordered paint operations, shared by the frame and device-free tests.</summary>
internal interface ITooltipSink
{
    void Fill(in Rect bounds, Vector4 color);
    void Border(in Rect bounds, float thickness, Vector4 color);
    void Sprite(Texture2D texture, in Rect destination, Vector4 sourceUV, Vector4 tint);
    void Text(bool titleFont, string text, Vector2 position, Vector4 color, float scale);
    void TextRuns(bool titleFont, ReadOnlySpan<ColoredTextRun> runs, Vector2 position, float scale, float opacity);
}

/// <summary>Pass through to existing draws. Constrained generic dispatch avoids boxing this frame adapter.</summary>
internal readonly struct SpriteBatchTooltipSink : ITooltipSink
{
    readonly SpriteBatch _batch;
    readonly Texture2D _white;
    readonly SpriteFont _titleFont, _bodyFont;

    internal SpriteBatchTooltipSink(SpriteBatch batch, Texture2D white, SpriteFont titleFont, SpriteFont bodyFont)
    {
        _batch = batch;
        _white = white;
        _titleFont = titleFont;
        _bodyFont = bodyFont;
    }

    public void Fill(in Rect bounds, Vector4 color) => GuiDraw.Fill(_batch, _white, bounds, color);

    public void Border(in Rect bounds, float thickness, Vector4 color) =>
        GuiDraw.Border(_batch, _white, bounds, thickness, color);

    public void Sprite(Texture2D texture, in Rect destination, Vector4 sourceUV, Vector4 tint) =>
        _batch.Draw(texture, new Vector4(destination.X, destination.Y, destination.Width, destination.Height),
            sourceUV, (Color)tint);

    public void Text(bool titleFont, string text, Vector2 position, Vector4 color, float scale) =>
        _batch.DrawString(titleFont ? _titleFont : _bodyFont, text, position, (Color)color, scale);

    public void TextRuns(bool titleFont, ReadOnlySpan<ColoredTextRun> runs, Vector2 position, float scale, float opacity) =>
        _batch.DrawStringRuns(titleFont ? _titleFont : _bodyFont, runs, position, scale, opacity);
}
