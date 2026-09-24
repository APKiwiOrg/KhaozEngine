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

    internal static List<TooltipLine> Wrap(ITextMeasurer font, TooltipLine source, float budget)
    {
        List<string> wrapped = TextLayout.Wrap(font, source.Text, budget,
            hardBreak: true, preserveSpaceRuns: true);
        var visual = new List<TooltipLine>(wrapped.Count);
        int cursor = 0;
        foreach (string text in wrapped)
        {
            // The wrapper only removes separators at a line break. Its emitted text is otherwise an exact
            // source slice, including spaces within a line, so a forward search locates its colour spans.
            int start = source.Text.IndexOf(text, cursor, StringComparison.Ordinal);
            if (start < 0)
                throw new InvalidOperationException("A wrapped tooltip line is not a source text slice.");
            int end = start + text.Length;
            var runs = new List<ColoredTextRun>();
            int sourceAt = 0;
            foreach (ColoredTextRun run in source.Runs.Span)
            {
                int runEnd = sourceAt + run.Text.Length;
                int from = Math.Max(start, sourceAt);
                int through = Math.Min(end, runEnd);
                if (from < through)
                    runs.Add(new ColoredTextRun(source.Text[from..through], run.Color));
                sourceAt = runEnd;
            }
            visual.Add(new TooltipLine(text, source.Color, source.Scale) { Runs = runs.ToArray() });
            cursor = end;
        }
        return visual;
    }
}
