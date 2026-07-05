using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>A single line of text in a <see cref="Tooltip"/>.</summary>
    public readonly record struct TooltipLine(string Text, Vector4 Color)
    {
        /// <summary>Build a line from localized text (resolved now against the ambient catalog).</summary>
        public static TooltipLine Of(LocalizedText text, Vector4 color) => new(text.Resolve(), color);
    }

    /// <summary>
    /// How a <see cref="Tooltip"/> decides to hide itself. <see cref="CallerDriven"/> (default) leaves visibility
    /// entirely to <see cref="Tooltip.Show(LocalizedText, IReadOnlyList{TooltipLine}, Vector2)"/>/<see cref="Tooltip.Hide"/> (desktop hover). <see cref="TapOutside"/>
    /// makes <see cref="Tooltip.Update"/> auto-dismiss on the next tap released outside the bubble (touch/mobile),
    /// so the dismissal policy is a runtime value, not a compile-time platform branch.
    /// </summary>
    public enum TooltipDismiss { CallerDriven, TapOutside }

    /// <summary>Spacing/padding knobs for tooltip auto-sizing and edge clamping.</summary>
    public struct TooltipMetrics
    {
        public float PadX, PadY, TitleGap, LineSpacing, AnchorOffsetY, Margin, TopMargin;
        /// <summary>Gap between the left title and the optional right-aligned title value (two-column header).</summary>
        public float TitleRightGap;
        public static TooltipMetrics Default => new()
        { PadX = 10, PadY = 8, TitleGap = 5, LineSpacing = 3, AnchorOffsetY = 10, Margin = 4, TopMargin = 0, TitleRightGap = 12 };
    }

    /// <summary>
    /// A floating, auto-sized text bubble anchored near a point. <see cref="ComputeBounds(ITextMeasurer, string, ITextMeasurer, IReadOnlyList{TooltipLine}, Vector2, Vector2, TooltipMetrics)"/> is a pure layout
    /// function (sizes to content, prefers above the anchor, flips below when there's no room, clamps into the
    /// viewport) testable with a fake <see cref="ITextMeasurer"/>. The instance API is <see cref="Show(LocalizedText, IReadOnlyList{TooltipLine}, Vector2)"/> /
    /// <see cref="Hide"/> + <see cref="Draw"/>. The top margin is configurable via <see cref="TooltipMetrics"/>.
    /// <para>
    /// Opt-in extras (all default to the pre-existing look): a two-column title (<see cref="Show(string,string,IReadOnlyList{TooltipLine},Vector2)"/>
    /// with a right-aligned value), a separator under the title (<see cref="ShowTitleSeparator"/>), and touch/mobile
    /// auto-dismiss (<see cref="Dismiss"/> = <see cref="TooltipDismiss.TapOutside"/> + calling <see cref="Update"/>).
    /// </para>
    /// </summary>
    public sealed class Tooltip
    {
        readonly SpriteFont _titleFont, _bodyFont;

        string _title = "";
        string _titleRight = "";
        readonly List<TooltipLine> _lines = new();
        Vector2 _anchor;
        bool _showedThisFrame;

        public bool IsVisible { get; private set; }
        public TooltipMetrics Metrics = TooltipMetrics.Default;

        /// <summary>
        /// Dismissal policy. Default <see cref="TooltipDismiss.CallerDriven"/> keeps the pre-existing behaviour
        /// (visibility is the caller's job). Set <see cref="TooltipDismiss.TapOutside"/> and call <see cref="Update"/>
        /// each frame for touch/mobile auto-dismiss on the next tap-outside.
        /// </summary>
        public TooltipDismiss Dismiss = TooltipDismiss.CallerDriven;
        /// <summary>
        /// The design-space viewport the bubble clamps within. Defaults to <see cref="Vector2.Zero"/> ("unset");
        /// the caller must assign the real design size before drawing. A visible tooltip with this unset throws
        /// in <see cref="Draw"/> so a forgotten assignment fails loudly instead of silently mis-positioning.
        /// </summary>
        public Vector2 Viewport = Vector2.Zero;

        public Vector4 Background = new(0.055f, 0.055f, 0.094f, 0.94f);
        public Vector4 Border = new(0.24f, 0.255f, 0.31f, 0.78f);
        public Vector4 TitleColor = new(0.86f, 0.88f, 0.94f, 1f);

        /// <summary>
        /// Opt-in: draw a 1px separator line under the title (in the <see cref="TooltipMetrics.TitleGap"/> band).
        /// Default <c>false</c> so existing tooltips render unchanged. Colour is <see cref="SeparatorColor"/>.
        /// </summary>
        public bool ShowTitleSeparator = false;
        public Vector4 SeparatorColor = new(0.20f, 0.22f, 0.27f, 0.63f);
        /// <summary>Colour of the optional right-aligned title value (two-column header). Drawn with the body font.</summary>
        public Vector4 TitleRightColor = new(0.71f, 0.71f, 0.75f, 1f);

        public Tooltip(SpriteFont titleFont, SpriteFont bodyFont) { _titleFont = titleFont; _bodyFont = bodyFont; }

        /// <summary>Show with a localized title + body lines, anchored near <paramref name="anchor"/> (in pixels).</summary>
        public void Show(LocalizedText title, IReadOnlyList<TooltipLine> lines, Vector2 anchor) =>
            Show(title, LocalizedText.Raw(""), lines, anchor);

        /// <summary>
        /// Show with a two-column localized title (<paramref name="title"/> left, <paramref name="titleRight"/>
        /// right-aligned on the same row) + body lines, anchored near <paramref name="anchor"/>. Pass an empty
        /// <see cref="LocalizedText"/> for a single-column title.
        /// </summary>
        public void Show(LocalizedText title, LocalizedText titleRight, IReadOnlyList<TooltipLine> lines, Vector2 anchor)
        {
            _title = title.Resolve() ?? "";
            _titleRight = titleRight.Resolve() ?? "";
            _lines.Clear();
            for (int i = 0; i < lines.Count; i++) _lines.Add(lines[i]);
            _anchor = anchor;
            IsVisible = true;
            _showedThisFrame = true;
        }

        /// <summary>Obsolete: pass a <see cref="LocalizedText"/> title. A raw string bypasses localization.</summary>
        [Obsolete("Pass a LocalizedText title; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...).")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public void Show(string title, IReadOnlyList<TooltipLine> lines, Vector2 anchor) =>
            Show(LocalizedText.Raw(title), LocalizedText.Raw(""), lines, anchor);

        /// <summary>Obsolete: pass <see cref="LocalizedText"/> titles. A raw string bypasses localization.</summary>
        [Obsolete("Pass a LocalizedText title; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...).")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public void Show(string title, string titleRight, IReadOnlyList<TooltipLine> lines, Vector2 anchor) =>
            Show(LocalizedText.Raw(title), LocalizedText.Raw(titleRight), lines, anchor);

        public void Hide() => IsVisible = false;

        /// <summary>
        /// Auto-dismiss driver for <see cref="TooltipDismiss.TapOutside"/> mode: hides on the next tap released
        /// outside the bubble. A no-op in <see cref="TooltipDismiss.CallerDriven"/> mode and on the frame the tooltip
        /// was shown (so the tap that opened it does not immediately close it). Call once per frame. Needs
        /// <see cref="Viewport"/> set (same as <see cref="Draw"/>).
        /// </summary>
        public void Update(Pointer pointer)
        {
            if (!IsVisible) { _showedThisFrame = false; return; }
            bool shownFrame = _showedThisFrame;
            _showedThisFrame = false;
            Rect bounds = ComputeBounds(_titleFont, _title, _titleRight, _bodyFont, _bodyFont, _lines, _anchor, Viewport, Metrics);
            if (ShouldDismiss(Dismiss, shownFrame, pointer, bounds)) IsVisible = false;
        }

        /// <summary>
        /// Pure dismissal decision: <c>true</c> only in <see cref="TooltipDismiss.TapOutside"/> mode, when it is not
        /// the frame the tooltip was shown, and <paramref name="pointer"/> just released a tap outside
        /// <paramref name="bounds"/> (the press-origin click-through invariant). Headless-testable.
        /// </summary>
        public static bool ShouldDismiss(TooltipDismiss mode, bool showedThisFrame, Pointer pointer, Rect bounds) =>
            mode == TooltipDismiss.TapOutside && !showedThisFrame && pointer.IsReleasedOutside(bounds);

        /// <summary>
        /// Pure layout: the on-screen rect for a tooltip with this content/anchor. Sizes to content, sits above
        /// the anchor (flips below if it would cross <see cref="TooltipMetrics.TopMargin"/>), clamps to the viewport.
        /// </summary>
        public static Rect ComputeBounds(ITextMeasurer titleFont, string title, ITextMeasurer bodyFont,
            IReadOnlyList<TooltipLine> lines, Vector2 anchor, Vector2 viewport, TooltipMetrics m) =>
            ComputeBounds(titleFont, title, "", bodyFont, bodyFont, lines, anchor, viewport, m);

        /// <summary>
        /// Two-column overload: as above, but the title row is <paramref name="title"/> (left) plus
        /// <paramref name="titleRight"/> (right-aligned, measured with <paramref name="titleRightFont"/>), so the
        /// bubble widens to fit both with a <see cref="TooltipMetrics.TitleRightGap"/> between them. Pass
        /// <paramref name="titleRight"/> <c>""</c> for a single-column title (identical to the 7-arg overload).
        /// </summary>
        public static Rect ComputeBounds(ITextMeasurer titleFont, string title, string titleRight,
            ITextMeasurer titleRightFont, ITextMeasurer bodyFont,
            IReadOnlyList<TooltipLine> lines, Vector2 anchor, Vector2 viewport, TooltipMetrics m)
        {
            float contentW = 0f;
            bool hasTitle = !string.IsNullOrEmpty(title);
            float titleRowW = hasTitle ? titleFont.Measure(title).X : 0f;
            if (!string.IsNullOrEmpty(titleRight))
                titleRowW += m.TitleRightGap + titleRightFont.Measure(titleRight).X;
            contentW = MathF.Max(contentW, titleRowW);
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
            if (Viewport == Vector2.Zero)
                throw new InvalidOperationException(
                    "Tooltip.Viewport is unset (Vector2.Zero); assign the design viewport size before draw.");
            Rect b = ComputeBounds(_titleFont, _title, _titleRight, _bodyFont, _bodyFont, _lines, _anchor, Viewport, Metrics);
            GuiDraw.Fill(batch, white, b, Background);
            GuiDraw.Border(batch, white, b, 1f, Border);

            float x = b.X + Metrics.PadX;
            float y = b.Y + Metrics.PadY;
            if (!string.IsNullOrEmpty(_title))
            {
                batch.DrawString(_titleFont, _title, new Vector2(MathF.Floor(x), MathF.Floor(y)), (Color)TitleColor);
                if (!string.IsNullOrEmpty(_titleRight))
                {
                    float rw = _bodyFont.Measure(_titleRight).X;
                    batch.DrawString(_bodyFont, _titleRight,
                        new Vector2(MathF.Floor(b.Right - Metrics.PadX - rw), MathF.Floor(y)), (Color)TitleRightColor);
                }
                y += _titleFont.LineHeight + Metrics.TitleGap;
                if (ShowTitleSeparator)
                {
                    float sepY = MathF.Floor(y - Metrics.TitleGap * 0.5f);
                    GuiDraw.Fill(batch, white, new Rect(b.X + Metrics.PadX, sepY, b.Width - Metrics.PadX * 2f, 1f), SeparatorColor);
                }
            }
            for (int i = 0; i < _lines.Count; i++)
            {
                batch.DrawString(_bodyFont, _lines[i].Text, new Vector2(MathF.Floor(x), MathF.Floor(y)), (Color)_lines[i].Color);
                y += _bodyFont.LineHeight + Metrics.LineSpacing;
            }
        }
    }
}
