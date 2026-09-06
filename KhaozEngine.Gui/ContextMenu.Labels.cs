using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui;

public sealed partial class ContextMenu
{
    internal readonly record struct LabelRun(string Text, float X, Vector4 Color);

    internal static LabelRun[] LayoutLabel(
        ContextMenuEntry entry,
        ITextMeasurer font,
        float startX,
        Vector4 defaultColor,
        Vector4 disabledColor)
    {
        IReadOnlyList<LabelSegment>? segments = entry.LabelSegments;
        if (segments is null)
        {
            Vector4 color = entry.Enabled ? entry.LabelColor ?? defaultColor : disabledColor;
            return new[] { new LabelRun(entry.Label, startX, color) };
        }

        var runs = new LabelRun[segments.Count];
        float x = startX;
        for (int i = 0; i < segments.Count; i++)
        {
            LabelSegment segment = segments[i];
            string text = segment.Content.Resolve() ?? "";
            Vector4 color = entry.Enabled ? segment.Color ?? entry.LabelColor ?? defaultColor : disabledColor;
            runs[i] = new LabelRun(text, x, color);
            x += font.Measure(text).X;
        }
        return runs;
    }

    private static float MeasureLabel(ITextMeasurer font, ContextMenuEntry entry)
    {
        IReadOnlyList<LabelSegment>? segments = entry.LabelSegments;
        if (segments is null) return font.Measure(entry.Label).X;

        float width = 0f;
        for (int i = 0; i < segments.Count; i++)
        {
            width += font.Measure(segments[i].Content.Resolve() ?? "").X;
        }
        return width;
    }
}
