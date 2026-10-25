using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui;

/// <summary>A measured body row with positions relative to the body's top-left corner.</summary>
internal readonly record struct TooltipVisualLine(
    TooltipLine Line, Vector2 TextPosition, Rect? IconBounds, float Width, float Height);

/// <summary>The shared body measurement and placement consumed by tooltip bounds and drawing.</summary>
internal sealed record TooltipBodyLayout(IReadOnlyList<TooltipVisualLine> Lines, float Width, float Height)
{
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
            if (line.Icon is TooltipIcon icon && (!float.IsFinite(icon.Size) || icon.Size <= 0f))
                throw new ArgumentOutOfRangeException(nameof(icon.Size), "Icon size must be finite and positive.");
        }

        List<TooltipLine> visual = WrapBody(titleFont, bodyFont, lines, maxContentWidth);
        var rows = new List<TooltipVisualLine>(visual.Count);
        float width = 0f;
        float height = 0f;
        for (int i = 0; i < visual.Count; i++)
        {
            TooltipLine line = visual[i];
            ITextMeasurer font = line.TitleFont ? titleFont : bodyFont;
            float textHeight = font.LineHeight * line.Scale;
            float rowHeight = textHeight;
            float inset = 0f;
            float textOffsetY = 0f;
            Rect? iconBounds = null;
            height += line.GapBefore;
            if (line.Icon is TooltipIcon icon)
            {
                rowHeight = MathF.Max(textHeight, icon.Size);
                inset = icon.Size + metrics.IconGap;
                textOffsetY = (rowHeight - textHeight) * 0.5f;
                iconBounds = new Rect(0f, height + (rowHeight - icon.Size) * 0.5f, icon.Size, icon.Size);
            }
            float rowWidth = inset + font.Measure(line.Text).X * line.Scale;
            rows.Add(new TooltipVisualLine(line, new Vector2(inset, height + textOffsetY), iconBounds, rowWidth, rowHeight));
            width = MathF.Max(width, rowWidth);
            height += rowHeight + metrics.LineSpacing;
        }
        if (rows.Count > 0) height -= metrics.LineSpacing;
        return new TooltipBodyLayout(rows, width, height);
    }

    // Preserve the existing unadorned wrapping and nonpositive-scale fallback while moving it into layout.
    static List<TooltipLine> WrapBody(ITextMeasurer titleFont, ITextMeasurer bodyFont,
        IReadOnlyList<TooltipLine> lines, float maxContentWidth)
    {
        bool bounded = maxContentWidth > 0f && !float.IsInfinity(maxContentWidth);
        var outLines = new List<TooltipLine>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            TooltipLine line = lines[i];
            string text = line.Text ?? "";
            if (line.Text is null) line = line with { Text = text };
            ITextMeasurer font = line.TitleFont ? titleFont : bodyFont;
            float budget = bounded ? maxContentWidth / (line.Scale > 0f ? line.Scale : 1f) : maxContentWidth;
            if (!bounded || font.Measure(text).X <= budget)
            {
                outLines.Add(line);
                continue;
            }
            if (!line.Runs.IsEmpty)
            {
                outLines.AddRange(TooltipColoredLines.Wrap(font, line, budget));
                continue;
            }
            foreach (string wrapped in TextLayout.Wrap(font, text, budget, hardBreak: true))
                outLines.Add(new TooltipLine(wrapped, line.Color, line.Scale));
        }
        return outLines;
    }
}
