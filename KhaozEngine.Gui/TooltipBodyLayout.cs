using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui;

/// <summary>A measured body row with positioned text and icon geometry.</summary>
internal readonly record struct TooltipVisualLine(
    TooltipLine Line, Vector2 TextPosition, Rect? IconBounds, float Width, float Height)
{
    internal float TextOffsetY { get; init; }

    // Advance the absolute cursor one row at a time so fractional scales retain their legacy text pixels.
    internal TooltipVisualLine Place(ref Vector2 cursor, float lineSpacing)
    {
        cursor.Y += Line.GapBefore;
        TooltipVisualLine placed = this with
        {
            TextPosition = new Vector2(cursor.X + TextPosition.X, cursor.Y + TextOffsetY),
            IconBounds = IconBounds is Rect icon
                ? new Rect(cursor.X, cursor.Y + (Height - icon.Height) * 0.5f, icon.Width, icon.Height)
                : null,
        };
        cursor.Y += Height + lineSpacing;
        return placed;
    }
}

/// <summary>The shared body measurement and placement consumed by tooltip bounds and drawing.</summary>
internal sealed record TooltipBodyLayout(IReadOnlyList<TooltipVisualLine> Lines, float Width, float Height)
{
    // Seed with the main title height. Adding a pre-summed body changes legacy fractional-scale bounds.
    internal float AccumulateHeight(float height, float lineSpacing)
    {
        for (int i = 0; i < Lines.Count; i++)
        {
            height += Lines[i].Line.GapBefore;
            height += Lines[i].Height + lineSpacing;
        }
        return Lines.Count > 0 ? height - lineSpacing : height;
    }

    internal static TooltipBodyLayout Build(ITextMeasurer titleFont, ITextMeasurer bodyFont,
        IReadOnlyList<TooltipLine> lines, in TooltipMetrics metrics, float maxContentWidth)
    {
        if (!float.IsFinite(metrics.IconGap) || metrics.IconGap < 0f)
            throw new ArgumentOutOfRangeException(nameof(metrics.IconGap), "Icon gap must be finite and nonnegative.");
        for (int i = 0; i < lines.Count; i++)
        {
            TooltipLine line = lines[i];
            if (!float.IsFinite(line.GapBefore) || line.GapBefore < 0f)
                throw new ArgumentOutOfRangeException(nameof(line.GapBefore), "Paragraph gap must be finite and nonnegative.");
            if (line.Icon is TooltipIcon icon)
            {
                if (!float.IsFinite(icon.Size) || icon.Size <= 0f)
                    throw new ArgumentOutOfRangeException(nameof(icon.Size), "Icon size must be finite and positive.");
                if (float.IsFinite(maxContentWidth) && maxContentWidth <= icon.Size + metrics.IconGap)
                    throw new ArgumentOutOfRangeException(nameof(maxContentWidth),
                        "Content width must leave a positive text budget after the icon inset.");
            }
        }

        var visual = WrapBody(titleFont, bodyFont, lines, metrics.IconGap, maxContentWidth);
        var rows = new List<TooltipVisualLine>(visual.Count);
        float width = 0f;
        var cursor = Vector2.Zero;
        for (int i = 0; i < visual.Count; i++)
        {
            (TooltipLine line, float inset) = visual[i];
            ITextMeasurer font = line.TitleFont ? titleFont : bodyFont;
            float textHeight = font.LineHeight * line.Scale;
            float rowHeight = textHeight;
            float textOffsetY = 0f;
            Rect? iconBounds = null;
            if (line.Icon is TooltipIcon icon)
            {
                rowHeight = MathF.Max(textHeight, icon.Size);
                textOffsetY = (rowHeight - textHeight) * 0.5f;
                iconBounds = new Rect(0f, (rowHeight - icon.Size) * 0.5f, icon.Size, icon.Size);
            }
            float rowWidth = inset + font.Measure(line.Text).X * line.Scale;
            var row = new TooltipVisualLine(line, new Vector2(inset, textOffsetY), iconBounds, rowWidth, rowHeight)
            {
                TextOffsetY = textOffsetY,
            };
            rows.Add(row.Place(ref cursor, metrics.LineSpacing));
            width = MathF.Max(width, rowWidth);
        }
        float height = cursor.Y;
        if (rows.Count > 0) height -= metrics.LineSpacing;
        return new TooltipBodyLayout(rows, width, height);
    }

    // Keep the source inset on every visual row, independently of its first-row-only icon.
    static List<(TooltipLine Line, float Inset)> WrapBody(ITextMeasurer titleFont, ITextMeasurer bodyFont,
        IReadOnlyList<TooltipLine> lines, float iconGap, float maxContentWidth)
    {
        bool bounded = maxContentWidth > 0f && !float.IsInfinity(maxContentWidth);
        var outLines = new List<(TooltipLine Line, float Inset)>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            TooltipLine line = lines[i];
            string text = line.Text ?? "";
            if (line.Text is null) line = line with { Text = text };
            ITextMeasurer font = line.TitleFont ? titleFont : bodyFont;
            float inset = line.Icon is TooltipIcon icon ? icon.Size + iconGap : 0f;
            float budget = bounded
                ? (maxContentWidth - inset) / (line.Scale > 0f ? line.Scale : 1f)
                : maxContentWidth;
            if (!bounded || font.Measure(text).X <= budget)
            {
                outLines.Add((line, inset));
                continue;
            }
            if (!line.Runs.IsEmpty)
            {
                foreach (TooltipLine wrapped in TooltipColoredLines.Wrap(font, line, budget))
                    outLines.Add((wrapped, inset));
                continue;
            }
            foreach (string wrapped in TextLayout.Wrap(font, text, budget, hardBreak: true))
            {
                outLines.Add((line with { Text = wrapped }, inset));
                line = line with { Icon = null, GapBefore = 0f };
            }
        }
        return outLines;
    }
}
