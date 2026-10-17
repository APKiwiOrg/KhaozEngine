using System;
using System.Collections.Generic;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui
{
    /// <summary>One visual line of wrapped text as a UTF-16 range. <see cref="Length"/> excludes the line's
    /// ending <c>'\n'</c>.</summary>
    internal readonly record struct TextAreaLine(int Start, int Length);

    /// <summary>
    /// Greedy word wrap and caret geometry for multi-line text. A <c>'\n'</c> ends a line. Words wrap after the
    /// last space that fits, trailing spaces stay on their line, and a word wider than the line hard breaks
    /// between scalars. Every line holds at least one scalar and a surrogate pair is never split. A width that is
    /// not positive puts every scalar on its own line. Each word and space run is measured once and the widths are
    /// summed, so a long text lays out in linear time. Caret X positions use the same summation as the wrap.
    /// </summary>
    internal static class TextAreaLayout
    {
        internal static IReadOnlyList<TextAreaLine> Wrap(ITextMeasurer font, string text, float width, float scale = 1f)
        {
            var lines = new List<TextAreaLine>();
            int start = 0;
            while (true)
            {
                int newline = text.IndexOf('\n', start);
                int stop = newline < 0 ? text.Length : newline;
                if (width > 0f) WrapParagraph(font, text, start, stop, width, scale, lines);
                else SplitScalars(text, start, stop, lines);
                if (newline < 0) return lines;
                start = newline + 1;
            }
        }

        /// <summary>The visual line and X offset of <paramref name="caret"/>. A caret at a soft wrap sits at the
        /// start of the following line.</summary>
        internal static (int Line, float X) CaretPosition(ITextMeasurer font, string text, IReadOnlyList<TextAreaLine> lines,
            int caret, float scale = 1f)
        {
            if (lines.Count == 0) return (0, 0f);
            caret = Math.Clamp(caret, 0, text.Length);
            int line = LineOf(lines, caret);
            TextAreaLine l = lines[line];
            int end = Math.Min(caret, l.Start + l.Length);
            float x = 0f;
            for (int i = l.Start; i < end;)
            {
                int runEnd = RunEnd(text, i, end);
                x += font.Measure(text.AsSpan(i, runEnd - i)).X * scale;
                i = runEnd;
            }
            return (line, x);
        }

        /// <summary>The scalar boundary on <paramref name="line"/> nearest to <paramref name="x"/>. The end of a
        /// soft wrapped line renders on the next line, so it is never returned for this one.</summary>
        internal static int IndexAt(ITextMeasurer font, string text, IReadOnlyList<TextAreaLine> lines, int line, float x,
            float scale = 1f)
        {
            if (lines.Count == 0) return 0;
            line = Math.Clamp(line, 0, lines.Count - 1);
            TextAreaLine l = lines[line];
            int end = Math.Min(l.Start + l.Length, text.Length);
            if (l.Length > 0 && line + 1 < lines.Count && lines[line + 1].Start == end)
                end = PreviousBoundary(text, l.Start, end);
            float left = 0f;
            for (int i = l.Start; i < end;)
            {
                int n = ScalarLength(text, i, end);
                float w = font.Measure(text.AsSpan(i, n)).X * scale;
                if (x < left + w / 2f) return i;
                left += w;
                i += n;
            }
            return end;
        }

        static void WrapParagraph(ITextMeasurer font, string text, int start, int stop, float width, float scale,
            List<TextAreaLine> lines)
        {
            int lineStart = start;
            float lineWidth = 0f;
            for (int i = start; i < stop;)
            {
                int runEnd = RunEnd(text, i, stop);
                float w = font.Measure(text.AsSpan(i, runEnd - i)).X * scale;
                if (text[i] == ' ' || lineWidth + w <= width)
                {
                    lineWidth += w;
                }
                else
                {
                    if (i > lineStart)
                    {
                        lines.Add(new TextAreaLine(lineStart, i - lineStart));
                        lineStart = i;
                    }
                    lineWidth = w <= width ? w : HardBreak(font, text, i, runEnd, width, scale, lines, ref lineStart);
                }
                i = runEnd;
            }
            lines.Add(new TextAreaLine(lineStart, stop - lineStart));
        }

        /// <summary>Breaks a word wider than the line between scalars, measuring each scalar once. Starts on an
        /// empty line and returns the width of the last, still open line.</summary>
        static float HardBreak(ITextMeasurer font, string text, int start, int end, float width, float scale,
            List<TextAreaLine> lines, ref int lineStart)
        {
            float lineWidth = 0f;
            for (int i = start; i < end;)
            {
                int n = ScalarLength(text, i, end);
                float w = font.Measure(text.AsSpan(i, n)).X * scale;
                if (i > lineStart && lineWidth + w > width)
                {
                    lines.Add(new TextAreaLine(lineStart, i - lineStart));
                    lineStart = i;
                    lineWidth = 0f;
                }
                lineWidth += w;
                i += n;
            }
            return lineWidth;
        }

        static void SplitScalars(string text, int start, int stop, List<TextAreaLine> lines)
        {
            if (start == stop) lines.Add(new TextAreaLine(start, 0));
            for (int i = start; i < stop;)
            {
                int n = ScalarLength(text, i, stop);
                lines.Add(new TextAreaLine(i, n));
                i += n;
            }
        }

        /// <summary>The last line starting at or before <paramref name="index"/>.</summary>
        static int LineOf(IReadOnlyList<TextAreaLine> lines, int index)
        {
            int lo = 0, hi = lines.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (lines[mid].Start <= index) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>The end of the space run or word run starting at <paramref name="i"/>.</summary>
        static int RunEnd(string text, int i, int stop)
        {
            bool space = text[i] == ' ';
            int j = i + 1;
            while (j < stop && (text[j] == ' ') == space) j++;
            return j;
        }

        static int ScalarLength(string text, int i, int stop) =>
            char.IsHighSurrogate(text[i]) && i + 1 < stop && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;

        static int PreviousBoundary(string text, int start, int end) =>
            end - 2 >= start && char.IsLowSurrogate(text[end - 1]) && char.IsHighSurrogate(text[end - 2]) ? end - 2 : end - 1;
    }
}
