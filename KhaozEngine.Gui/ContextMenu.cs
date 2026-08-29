using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// One selectable row in a <see cref="ContextMenu"/>. <see cref="Label"/> and <see cref="RightDetail"/> are
    /// RESOLVED strings: localization happens once, at construction, via <see cref="Of"/> (the
    /// <see cref="TooltipLine.Of"/> precedent), so the draw path never re-resolves. <see cref="LabelColor"/> /
    /// <see cref="DetailColor"/> are per-row overrides, <c>null</c> meaning "use the menu's colour".
    /// <see cref="Tag"/> is an opaque caller payload (an id, an enum cast to <see cref="long"/>) that rides
    /// through selection, and a row with <see cref="Enabled"/> <c>false</c> renders greyed and refuses selection.
    /// </summary>
    public readonly record struct ContextMenuEntry(
        string Label, string RightDetail = "", Vector4? LabelColor = null, Vector4? DetailColor = null,
        long Tag = 0, bool Enabled = true)
    {
        /// <summary>
        /// Build an entry from localized text, resolved now against the ambient catalog.
        /// <paramref name="rightDetail"/> defaults to <c>default(LocalizedText)</c>, which resolves to the empty
        /// string, so a row with no right-hand detail needs no extra ceremony.
        /// </summary>
        public static ContextMenuEntry Of(LocalizedText label, LocalizedText rightDetail = default,
            Vector4? labelColor = null, Vector4? detailColor = null, long tag = 0, bool enabled = true) =>
            new(label.Resolve() ?? "", rightDetail.Resolve() ?? "", labelColor, detailColor, tag, enabled);
    }

    /// <summary>Spacing and padding knobs for context-menu auto-sizing and edge clamping.</summary>
    public struct ContextMenuMetrics
    {
        /// <summary>Horizontal padding inside the menu, applied on both sides.</summary>
        public float PadX;
        /// <summary>Vertical padding above and below the text in the title band and in every entry row.</summary>
        public float RowPadY;
        /// <summary>Extra gap under the title band, before the first entry row.</summary>
        public float TitleGap;
        /// <summary>Minimum gap between a row's label and its right-aligned detail.</summary>
        public float DetailGap;
        /// <summary>Keep-out distance from every viewport edge when the menu is clamped into view.</summary>
        public float Margin;

        /// <summary>The default look: 10 / 4 / 5 / 16 / 4.</summary>
        public static ContextMenuMetrics Default => new()
        { PadX = 10, RowPadY = 4, TitleGap = 5, DetailGap = 16, Margin = 4 };
    }

    /// <summary>
    /// A right-click option menu anchored at a screen point (the OSRS-style option list): a title band over a
    /// stack of selectable rows. <see cref="ComputeBounds"/> and <see cref="RowBounds"/> are pure layout
    /// functions over <see cref="ITextMeasurer"/>, so the whole geometry is headless-testable with a fake
    /// measurer, exactly as <see cref="Tooltip.ComputeBounds(ITextMeasurer, string, ITextMeasurer, IReadOnlyList{TooltipLine}, Vector2, Vector2, TooltipMetrics)"/> is.
    /// </summary>
    public sealed class ContextMenu
    {
        readonly SpriteFont _titleFont, _bodyFont;

        /// <summary>Spacing knobs used by the layout and the draw. Defaults to <see cref="ContextMenuMetrics.Default"/>.</summary>
        public ContextMenuMetrics Metrics = ContextMenuMetrics.Default;

        /// <summary>Build a menu that measures and draws its title band with <paramref name="titleFont"/> and its entry rows with <paramref name="bodyFont"/>.</summary>
        public ContextMenu(SpriteFont titleFont, SpriteFont bodyFont) { _titleFont = titleFont; _bodyFont = bodyFont; }

        /// <summary>
        /// Pure layout: the on-screen rect for a menu with this title and these entries opened at
        /// <paramref name="point"/>. The menu's top-left sits AT the point and opens down-right, clamped into
        /// <paramref name="viewport"/> by <see cref="ContextMenuMetrics.Margin"/> on all four sides. When the
        /// bottom would overflow the viewport the menu flips to sit with its BOTTOM at the point instead, which
        /// mirrors the <see cref="Tooltip"/> flip.
        /// <para>
        /// Width is the widest of the title and every row (a row being its label plus, when it has a right
        /// detail, <see cref="ContextMenuMetrics.DetailGap"/> plus that detail), plus horizontal padding.
        /// Height is the title band plus one row per entry. The title band is ALWAYS present, so an empty title
        /// still draws its header band.
        /// </para>
        /// </summary>
        public static Rect ComputeBounds(ITextMeasurer titleFont, string title, ITextMeasurer bodyFont,
            IReadOnlyList<ContextMenuEntry> entries, Vector2 point, Vector2 viewport, ContextMenuMetrics m)
        {
            float contentW = string.IsNullOrEmpty(title) ? 0f : titleFont.Measure(title).X;
            for (int i = 0; i < entries.Count; i++)
            {
                ContextMenuEntry e = entries[i];
                float rowW = bodyFont.Measure(e.Label).X;
                if (!string.IsNullOrEmpty(e.RightDetail))
                    rowW += m.DetailGap + bodyFont.Measure(e.RightDetail).X;
                contentW = MathF.Max(contentW, rowW);
            }

            float w = contentW + m.PadX * 2f;
            float h = TitleBandHeight(titleFont, m) + entries.Count * RowHeight(bodyFont, m);

            float x = point.X;
            float y = point.Y;
            if (y + h > viewport.Y - m.Margin) y = point.Y - h;   // flip up: the point becomes the bottom edge

            x = Math.Clamp(x, m.Margin, MathF.Max(m.Margin, viewport.X - w - m.Margin));
            y = Math.Clamp(y, m.Margin, MathF.Max(m.Margin, viewport.Y - h - m.Margin));
            return new Rect(x, y, w, h);
        }

        /// <summary>
        /// The rect of entry <paramref name="i"/> within <paramref name="bounds"/>, which must have been
        /// computed by <see cref="ComputeBounds"/> from the same fonts and metrics. Rows are full-width and
        /// stack directly under the title band with no gaps, so hover hit-testing and drawing walk the same
        /// geometry.
        /// </summary>
        public static Rect RowBounds(Rect bounds, ITextMeasurer titleFont, ITextMeasurer bodyFont, int i,
            ContextMenuMetrics m)
        {
            float rowH = RowHeight(bodyFont, m);
            return new Rect(bounds.X, bounds.Y + TitleBandHeight(titleFont, m) + i * rowH, bounds.Width, rowH);
        }

        /// <summary>Height of the always-present title band, including the gap under it.</summary>
        internal static float TitleBandHeight(ITextMeasurer titleFont, ContextMenuMetrics m) =>
            titleFont.LineHeight + m.RowPadY * 2f + m.TitleGap;

        /// <summary>Height of one entry row.</summary>
        internal static float RowHeight(ITextMeasurer bodyFont, ContextMenuMetrics m) =>
            bodyFont.LineHeight + m.RowPadY * 2f;
    }
}
