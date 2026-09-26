using System;
using System.Collections.Generic;
using System.Text;

namespace KhaozEngine.Render2D;

/// <summary>One wrapped line whose resolved coloured runs concatenate to <see cref="Text"/>.</summary>
public readonly record struct ColoredTextLine(string Text, ReadOnlyMemory<ColoredTextRun> Runs);

/// <summary>Device-free wrapping for resolved coloured text runs.</summary>
public static class ColoredTextLayout
{
    /// <summary>
    /// Wrap adjacent <paramref name="runs"/> as one text block and project each wrapped source slice back onto
    /// its colours. Interior space runs are preserved. Set <paramref name="hardBreak"/> to split a token wider
    /// than <paramref name="maxWidth"/> at character boundaries.
    /// </summary>
    public static List<ColoredTextLine> Wrap(ITextMeasurer font, ReadOnlySpan<ColoredTextRun> runs,
        float maxWidth, bool hardBreak = false)
    {
        ArgumentNullException.ThrowIfNull(font);
        var source = new StringBuilder();
        for (int i = 0; i < runs.Length; i++) source.Append(runs[i].Text ?? "");
        string sourceText = source.ToString();
        List<string> wrapped = TextLayout.Wrap(font, sourceText, maxWidth,
            hardBreak: hardBreak, preserveSpaceRuns: true);
        var lines = new List<ColoredTextLine>(wrapped.Count);
        int cursor = 0;
        for (int lineIndex = 0; lineIndex < wrapped.Count; lineIndex++)
        {
            string text = wrapped[lineIndex];
            if (text.Length == 0)
            {
                lines.Add(new ColoredTextLine("", ReadOnlyMemory<ColoredTextRun>.Empty));
                continue;
            }

            int start = sourceText.IndexOf(text, cursor, StringComparison.Ordinal);
            if (start < 0)
                throw new InvalidOperationException("A wrapped coloured line is not a source text slice.");
            int end = start + text.Length;
            var lineRuns = new List<ColoredTextRun>();
            int sourceAt = 0;
            for (int runIndex = 0; runIndex < runs.Length; runIndex++)
            {
                ColoredTextRun run = runs[runIndex];
                string runText = run.Text ?? "";
                int runEnd = sourceAt + runText.Length;
                int from = Math.Max(start, sourceAt);
                int through = Math.Min(end, runEnd);
                if (from < through)
                    lineRuns.Add(new ColoredTextRun(sourceText[from..through], run.Color));
                sourceAt = runEnd;
            }
            lines.Add(new ColoredTextLine(text, lineRuns.ToArray()));
            cursor = end;
        }
        return lines;
    }
}
