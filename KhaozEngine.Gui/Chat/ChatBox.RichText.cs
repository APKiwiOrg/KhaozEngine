using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui.Chat;

public sealed partial class ChatBox
{
    void AddRichRows(ChatEntry entry, ITextMeasurer measurer, System.TimeZoneInfo timeZone, float wrapWidth)
    {
        Vector4 baseColor = SelectColor(entry, Theme);
        var source = new List<ColoredTextRun>();
        AddRun(source, FormatPrefix(entry, ShowTimestamps, timeZone), Theme.TimestampText);
        string author = entry.Author is { } value ? value.Resolve() : "";
        AddRun(source, author.Length > 0 ? author + ": " : "", baseColor);
        ColoredTextRun[] content = InlineMarkup.Resolve(
            entry.MarkupContent!.Value,
            Theme.InlineStyles ?? InlineTextStyles.Empty,
            baseColor);
        for (int i = 0; i < content.Length; i++) AddRun(source, content[i].Text, content[i].Color);
        AddRun(source, entry.RepeatCount > 1
            ? $" ({entry.RepeatCount.ToString(CultureInfo.InvariantCulture)})"
            : "", baseColor);

        List<ColoredTextLine> lines = ColoredTextLayout.Wrap(measurer, source.ToArray(), wrapWidth, hardBreak: true);
        for (int i = 0; i < lines.Count; i++)
        {
            ColoredTextLine line = lines[i];
            _rows.Add(new CachedRow(entry, "", "", 0f, IsRich: true, line.Runs));
            _cachedLines.Add(line.Text);
        }
    }

    static void AddRun(List<ColoredTextRun> runs, string text, Vector4 color)
    {
        if (text.Length == 0) return;
        if (runs.Count > 0 && runs[^1].Color == color)
        {
            ColoredTextRun previous = runs[^1];
            runs[^1] = previous with { Text = previous.Text + text };
            return;
        }
        runs.Add(new ColoredTextRun(text, color));
    }
}
