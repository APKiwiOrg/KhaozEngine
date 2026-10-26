using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui;

public sealed partial class Tooltip
{
    /// <summary>Draw the tooltip if visible. <paramref name="white"/> is a 1x1 white texture.</summary>
    public void Draw(SpriteBatch batch, Texture2D white)
    {
        var sink = new SpriteBatchTooltipSink(batch, white, _titleFont, _bodyFont);
        DrawTo(ref sink, _titleFont, _bodyFont);
    }

    /// <summary>Paint the shared layout through the frame adapter or a device-free recording sink.</summary>
    internal void DrawTo<TSink>(ref TSink sink, ITextMeasurer titleFont, ITextMeasurer bodyFont)
        where TSink : ITooltipSink
    {
        if (!IsVisible || (_lines.Count == 0 && string.IsNullOrEmpty(_title))) return;
        if (Viewport == Vector2.Zero)
            throw new InvalidOperationException(
                "Tooltip.Viewport is unset (Vector2.Zero); assign the design viewport size before draw.");
        float maxWidth = ResolveMaxWidth();
        Rect b = ComputeLayout(titleFont, _title, _titleRight, bodyFont, bodyFont, _lines, _anchor, Viewport,
            Metrics, maxWidth, TitleScale, AnchorMode, out TooltipBodyLayout body);
        if (Backdrop is TooltipBackdrop backdrop)
            TooltipBackdropPainter.Draw(ref sink, new Rect(b.X + 1f, b.Y + 1f, b.Width - 2f, b.Height - 2f),
                backdrop, Opacity);
        else
            sink.Fill(b, GuiDraw.WithOpacity(Background, Opacity));
        sink.Border(b, 1f, GuiDraw.WithOpacity(Border, Opacity));

        float x = b.X + Metrics.PadX;
        float y = b.Y + Metrics.PadY;
        if (!string.IsNullOrEmpty(_title))
        {
            sink.Text(true, _title, new Vector2(MathF.Floor(x), MathF.Floor(y)),
                GuiDraw.WithOpacity(TitleColor, Opacity), TitleScale);
            if (!string.IsNullOrEmpty(_titleRight))
            {
                // The body face shares TitleScale with the left title, including its right-edge measurement.
                float rw = bodyFont.Measure(_titleRight).X * TitleScale;
                sink.Text(false, _titleRight,
                    new Vector2(MathF.Floor(b.Right - Metrics.PadX - rw), MathF.Floor(y)),
                    GuiDraw.WithOpacity(TitleRightColor, Opacity), TitleScale);
            }
            y += titleFont.LineHeight * TitleScale + Metrics.TitleGap;
            if (ShowTitleSeparator)
            {
                float sepY = MathF.Floor(y - Metrics.TitleGap * 0.5f);
                sink.Fill(new Rect(b.X + Metrics.PadX, sepY, b.Width - Metrics.PadX * 2f, 1f),
                    GuiDraw.WithOpacity(SeparatorColor, Opacity));
            }
        }
        var cursor = new Vector2(x, y);
        for (int i = 0; i < body.Lines.Count; i++)
        {
            TooltipVisualLine row = body.Lines[i].Place(ref cursor, Metrics.LineSpacing);
            TooltipLine line = row.Line;
            if (line.Icon is TooltipIcon icon && row.IconBounds is Rect iconBounds)
                sink.Sprite(icon.Texture, iconBounds, icon.SourceUV, GuiDraw.WithOpacity(Vector4.One, Opacity));
            Vector2 textPosition = new(MathF.Floor(row.TextPosition.X), MathF.Floor(row.TextPosition.Y));
            if (line.Runs.IsEmpty)
                sink.Text(line.TitleFont, line.Text, textPosition, GuiDraw.WithOpacity(line.Color, Opacity), line.Scale);
            else
                sink.TextRuns(line.TitleFont, line.Runs.Span, textPosition, line.Scale, Opacity);
        }
    }
}
