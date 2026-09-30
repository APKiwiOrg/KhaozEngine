using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui;

/// <summary>Resolves localized label segments once and maps wrapped text back to their source colours.</summary>
internal static class TooltipColoredLines
{
    internal static TooltipLine FromSegments(IReadOnlyList<LabelSegment> segments,
        Vector4 defaultColor, float scale)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var runs = new List<ColoredTextRun>(segments.Count);
        var text = new StringBuilder();
        foreach (LabelSegment segment in segments)
        {
            string piece = segment.Content.Resolve() ?? "";
            if (piece.Length == 0) continue;
            text.Append(piece);
            runs.Add(new ColoredTextRun(piece, segment.Color ?? defaultColor));
        }
        return new TooltipLine(text.ToString(), defaultColor, scale) { Runs = runs.ToArray() };
    }

    internal static TooltipLine FromMarkup(MarkupText content, InlineTextStyles styles,
        Vector4 defaultColor, float scale)
    {
        ColoredTextRun[] runs = InlineMarkup.Resolve(content, styles, defaultColor);
        var text = new StringBuilder();
        for (int i = 0; i < runs.Length; i++) text.Append(runs[i].Text);
        return new TooltipLine(text.ToString(), defaultColor, scale) { Runs = runs };
    }

    internal static List<TooltipLine> Wrap(ITextMeasurer font, TooltipLine source, float budget)
    {
        List<ColoredTextLine> wrapped = ColoredTextLayout.Wrap(font, source.Runs.Span, budget, hardBreak: true);
        var visual = new List<TooltipLine>(wrapped.Count);
        foreach (ColoredTextLine line in wrapped)
        {
            visual.Add(new TooltipLine(line.Text, source.Color, source.Scale) { Runs = line.Runs });
        }
        return visual;
    }
}
