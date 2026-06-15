using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>A single line of text in a <see cref="Tooltip"/>.</summary>
    public readonly record struct TooltipLine(string Text, Vector4 Color);

    /// <summary>Spacing/padding knobs for tooltip auto-sizing and edge clamping.</summary>
    public struct TooltipMetrics
    {
        public float PadX, PadY, TitleGap, LineSpacing, AnchorOffsetY, Margin, TopMargin;
        public static TooltipMetrics Default => new()
        { PadX = 10, PadY = 8, TitleGap = 5, LineSpacing = 3, AnchorOffsetY = 10, Margin = 4, TopMargin = 0 };
    }

    /// <summary>
    /// A floating, auto-sized text bubble anchored near a point. <see cref="ComputeBounds"/> is a pure layout
    /// function (sizes to content, prefers above the anchor, flips below when there's no room, clamps into the
    /// viewport) testable with a fake <see cref="ITextMeasurer"/>. The instance API is <see cref="Show"/> /
    /// <see cref="Hide"/> + <see cref="Draw"/>. Ported from the 4.x <c>UI.Tooltip</c> (game LayoutConstants dropped;
    /// the top margin is configurable via <see cref="TooltipMetrics"/>).
    /// </summary>
    public sealed class Tooltip
    {
        readonly SpriteFont _titleFont, _bodyFont;

        string _title = "";
        readonly List<TooltipLine> _lines = new();
        Vector2 _anchor;

        public bool IsVisible { get; private set; }
        public TooltipMetrics Metrics = TooltipMetrics.Default;
        public Vector2 Viewport = new(960, 540);

        public Vector4 Background = new(0.055f, 0.055f, 0.094f, 0.94f);
        public Vector4 Border = new(0.24f, 0.255f, 0.31f, 0.78f);
        public Vector4 TitleColor = new(0.86f, 0.88f, 0.94f, 1f);

        public Tooltip(SpriteFont titleFont, SpriteFont bodyFont) { _titleFont = titleFont; _bodyFont = bodyFont; }

        /// <summary>Show with a title + body lines, anchored near <paramref name="anchor"/> (in pixels).</summary>
        public void Show(string title, IReadOnlyList<TooltipLine> lines, Vector2 anchor)
        {
            _title = title ?? "";
            _lines.Clear();
            for (int i = 0; i < lines.Count; i++) _lines.Add(lines[i]);
            _anchor = anchor;
            IsVisible = true;
        }

        public void Hide() => IsVisible = false;

        /// <summary>
        /// Pure layout: the on-screen rect for a tooltip with this content/anchor. Sizes to content, sits above
        /// the anchor (flips below if it would cross <see cref="TooltipMetrics.TopMargin"/>), clamps to the viewport.
        /// </summary>
        public static Rect ComputeBounds(ITextMeasurer titleFont, string title, ITextMeasurer bodyFont,
            IReadOnlyList<TooltipLine> lines, Vector2 anchor, Vector2 viewport, TooltipMetrics m)
        {
            float contentW = 0f;
            bool hasTitle = !string.IsNullOrEmpty(title);
            if (hasTitle) contentW = MathF.Max(contentW, titleFont.Measure(title).X);
            for (int i = 0; i < lines.Count; i++)
                contentW = MathF.Max(contentW, bodyFont.Measure(lines[i].Text).X);

            float contentH = 0f;
            if (hasTitle) contentH += titleFont.LineHeight + m.TitleGap;
            contentH += lines.Count * (bodyFont.LineHeight + m.LineSpacing);
            if (lines.Count > 0) contentH -= m.LineSpacing;   // no trailing gap

            float w = contentW + m.PadX * 2f;
            float h = contentH + m.PadY * 2f;

            float x = anchor.X - w * 0.5f;
            float y = anchor.Y - h - m.AnchorOffsetY;          // above the anchor
            if (y < m.TopMargin) y = anchor.Y + m.AnchorOffsetY; // flip below

            x = Math.Clamp(x, m.Margin, MathF.Max(m.Margin, viewport.X - w - m.Margin));
            y = Math.Clamp(y, m.TopMargin, MathF.Max(m.TopMargin, viewport.Y - h - m.Margin));
            return new Rect(x, y, w, h);
        }

        /// <summary>Draw the tooltip if visible. <paramref name="white"/> is a 1x1 white texture.</summary>
        public void Draw(SpriteBatch batch, Texture2D white)
        {
            if (!IsVisible || (_lines.Count == 0 && string.IsNullOrEmpty(_title))) return;
            Rect b = ComputeBounds(_titleFont, _title, _bodyFont, _lines, _anchor, Viewport, Metrics);
            GuiDraw.Fill(batch, white, b, Background);
            GuiDraw.Border(batch, white, b, 1f, Border);

            float x = b.X + Metrics.PadX;
            float y = b.Y + Metrics.PadY;
            if (!string.IsNullOrEmpty(_title))
            {
                batch.DrawString(_titleFont, _title, new Vector2(MathF.Floor(x), MathF.Floor(y)), TitleColor);
                y += _titleFont.LineHeight + Metrics.TitleGap;
            }
            for (int i = 0; i < _lines.Count; i++)
            {
                batch.DrawString(_bodyFont, _lines[i].Text, new Vector2(MathF.Floor(x), MathF.Floor(y)), _lines[i].Color);
                y += _bodyFont.LineHeight + Metrics.LineSpacing;
            }
        }
    }
}
