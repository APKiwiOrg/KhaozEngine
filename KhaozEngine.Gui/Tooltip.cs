using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>A single line of text in a <see cref="Tooltip"/>, optionally at a per-line
    /// <paramref name="Scale"/> (default <c>1f</c>) so one font can render a size hierarchy.</summary>
    public readonly record struct TooltipLine(string Text, Vector4 Color, float Scale = 1f)
    {
        readonly string _text = Text;

        /// <summary>The resolved line text. Replacing it clears any runs tied to the previous text.</summary>
        public string Text
        {
            get => _text;
            init
            {
                _text = value;
                Runs = default;
            }
        }

        /// <summary>Resolved coloured runs for this line. Empty keeps the original uniform colour.</summary>
        public ReadOnlyMemory<ColoredTextRun> Runs { get; internal init; }

        /// <summary>Optional leading icon, drawn only on this source line's first visual row.
        /// Defaults to <c>null</c>, with no icon inset or gap.</summary>
        public TooltipIcon? Icon { get; init; }

        /// <summary>Use the tooltip's title font for this body line. Defaults to <c>false</c>.
        /// Text uses this line's <see cref="Scale"/>, independently of <see cref="Tooltip.TitleScale"/>.</summary>
        public bool TitleFont { get; init; }

        /// <summary>Space in design units before this source line's first visual row, including the first
        /// body line. Independent of <see cref="Scale"/>. Must be finite and nonnegative. Defaults to zero.</summary>
        public float GapBefore { get; init; }

        /// <summary>Build a line from localized text (resolved now against the ambient catalog),
        /// optionally at <paramref name="scale"/>.</summary>
        public static TooltipLine Of(LocalizedText text, Vector4 color, float scale = 1f) =>
            new(text.Resolve(), color, scale);

        /// <summary>Build a line from localized segments, retaining each segment's colour through wrapping.
        /// A segment without a colour uses <paramref name="defaultColor"/>.</summary>
        public static TooltipLine OfSegments(IReadOnlyList<LabelSegment> segments,
            Vector4 defaultColor, float scale = 1f) =>
            TooltipColoredLines.FromSegments(segments, defaultColor, scale);

        /// <summary>Build a line from trusted localized semantic colour markup. Missing style names inherit
        /// <paramref name="defaultColor"/>, and malformed markup renders literally.</summary>
        public static TooltipLine OfMarkup(MarkupText text, InlineTextStyles styles,
            Vector4 defaultColor, float scale = 1f) =>
            TooltipColoredLines.FromMarkup(text, styles, defaultColor, scale);
    }

    /// <summary>
    /// How a <see cref="Tooltip"/> decides to hide itself. <see cref="CallerDriven"/> (default) leaves visibility
    /// entirely to <see cref="Tooltip.Show(LocalizedText, IReadOnlyList{TooltipLine}, Vector2)"/>/<see cref="Tooltip.Hide"/> (desktop hover). <see cref="TapOutside"/>
    /// makes <see cref="Tooltip.Update"/> auto-dismiss on the next tap released outside the bubble (touch/mobile),
    /// so the dismissal policy is a runtime value, not a compile-time platform branch.
    /// </summary>
    public enum TooltipDismiss { CallerDriven, TapOutside }

    /// <summary>
    /// How a <see cref="Tooltip"/> places itself horizontally against its anchor. <see cref="Centered"/> (default)
    /// is the pre-existing placement: the bubble straddles the anchor (<c>x = anchor.X - width / 2</c>), which suits
    /// an anchor that is a widget's own centre. <see cref="Offset"/> is the cursor-style placement: the bubble's LEFT
    /// edge sits at <c>anchor.X + <see cref="TooltipMetrics.AnchorOffsetX"/></c>, so a positive offset puts the whole
    /// bubble to the right of a pointer instead of under it (negative puts it to the left). The vertical rule (prefer
    /// above the anchor by <see cref="TooltipMetrics.AnchorOffsetY"/>, flip below when there is no room) and the
    /// viewport clamp are the same in both modes.
    /// </summary>
    public enum TooltipAnchorMode { Centered, Offset }

    /// <summary>Spacing/padding knobs for tooltip auto-sizing, anchor placement and edge clamping.</summary>
    public struct TooltipMetrics
    {
        public float PadX, PadY, TitleGap, LineSpacing, AnchorOffsetY, Margin, TopMargin;
        /// <summary>Gap between the left title and the optional right-aligned title value (two-column header).</summary>
        public float TitleRightGap;
        /// <summary>
        /// Horizontal gap between the anchor and the bubble's left edge, used only in
        /// <see cref="TooltipAnchorMode.Offset"/> (the sibling of <see cref="AnchorOffsetY"/>). Default <c>0</c>,
        /// and unread in <see cref="TooltipAnchorMode.Centered"/>, so the pre-existing placement is untouched.
        /// </summary>
        public float AnchorOffsetX;
        /// <summary>Space in design units between a leading icon and its text. Independent of
        /// <see cref="TooltipLine.Scale"/>. Must be finite and nonnegative. Default <c>8f</c>.</summary>
        public float IconGap;
        public static TooltipMetrics Default => new()
        { PadX = 10, PadY = 8, TitleGap = 5, LineSpacing = 3, AnchorOffsetY = 10, Margin = 4, TopMargin = 0, TitleRightGap = 12, AnchorOffsetX = 0, IconGap = 8f };
    }

    /// <summary>
    /// A floating, auto-sized text bubble anchored near a point. <see cref="ComputeBounds(ITextMeasurer, string, ITextMeasurer, IReadOnlyList{TooltipLine}, Vector2, Vector2, TooltipMetrics)"/> is a pure layout
    /// function (sizes to content, prefers above the anchor, flips below when there's no room, clamps into the
    /// viewport) testable with a fake <see cref="ITextMeasurer"/>. The instance API is <see cref="Show(LocalizedText, IReadOnlyList{TooltipLine}, Vector2)"/> /
    /// <see cref="Hide"/> + <see cref="Draw"/>. The top margin is configurable via <see cref="TooltipMetrics"/>.
    /// <para>
    /// Opt-in extras (all default to the pre-existing look): a two-column title (<see cref="Show(LocalizedText,LocalizedText,IReadOnlyList{TooltipLine},Vector2)"/>
    /// with a right-aligned value), a separator under the title (<see cref="ShowTitleSeparator"/>), a width cap that
    /// word-wraps long body lines downward instead of overflowing (<see cref="MaxWidth"/> / <see cref="MaxWidthFraction"/>),
    /// touch/mobile auto-dismiss (<see cref="Dismiss"/> = <see cref="TooltipDismiss.TapOutside"/> + calling <see cref="Update"/>),
    /// a uniform fade (<see cref="Opacity"/>), and cursor-style placement beside the anchor rather than straddling it
    /// (<see cref="AnchorMode"/> = <see cref="TooltipAnchorMode.Offset"/> + <see cref="TooltipMetrics.AnchorOffsetX"/>).
    /// </para>
    /// </summary>
    public sealed partial class Tooltip
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
        /// Horizontal placement against the anchor. Default <see cref="TooltipAnchorMode.Centered"/> keeps the
        /// pre-existing behaviour (the bubble straddles the anchor), byte-identically. Set
        /// <see cref="TooltipAnchorMode.Offset"/> plus <see cref="TooltipMetrics.AnchorOffsetX"/> for the
        /// cursor-style placement, where the bubble sits beside the anchor rather than under it.
        /// </summary>
        public TooltipAnchorMode AnchorMode = TooltipAnchorMode.Centered;

        /// <summary>
        /// The design-space viewport the bubble clamps within. Defaults to <see cref="Vector2.Zero"/> ("unset");
        /// the caller must assign the real design size before drawing. A visible tooltip with this unset throws
        /// in <see cref="Draw"/> so a forgotten assignment fails loudly instead of silently mis-positioning.
        /// </summary>
        public Vector2 Viewport = Vector2.Zero;

        /// <summary>
        /// Absolute cap (design pixels) on the bubble width. A body line wider than this (minus horizontal
        /// padding) word-wraps into extra lines, growing the bubble downward, instead of widening it past the
        /// cap. Default <c>0</c> = unbounded (the pre-existing look: the bubble sizes to its widest line). When
        /// both this and <see cref="MaxWidthFraction"/> are set, the smaller resulting cap wins. The single-line
        /// title row is not wrapped; if it alone exceeds the cap the width is still capped and the title clips.
        /// </summary>
        public float MaxWidth = 0f;
        /// <summary>
        /// Cap on the bubble width as a fraction of <see cref="Viewport"/>.X (e.g. <c>0.4f</c> = 40% of the
        /// design width), so the cap tracks the resolution. Default <c>0</c> = unused. When both this and
        /// <see cref="MaxWidth"/> are set the smaller resulting cap wins; if neither is set the bubble is unbounded.
        /// </summary>
        public float MaxWidthFraction = 0f;

        public Vector4 Background = new(0.055f, 0.055f, 0.094f, 0.94f);

        /// <summary>Optional tiled fill inside the existing one-unit border. Defaults to <c>null</c>,
        /// keeping the flat <see cref="Background"/> fill. The caller owns the texture lifetime.</summary>
        public TooltipBackdrop? Backdrop { get; set; }

        public Vector4 Border = new(0.24f, 0.255f, 0.31f, 0.78f);
        public Vector4 TitleColor = new(0.86f, 0.88f, 0.94f, 1f);

        /// <summary>
        /// Uniform fade multiplied into every colour's alpha at draw time (1 = opaque). Lets a caller fade the whole
        /// bubble in/out with a host transition (a hover tooltip easing in, a panel sliding away under it). Default 1
        /// is a no-op. Mirrors <see cref="Dropdown.Opacity"/>. Every colour the bubble paints goes through it (the
        /// flat or tiled background, the border, the title row, the optional separator, icons and each body
        /// line), so <c>0</c> is fully transparent rather than leaving one element at full alpha.
        /// </summary>
        public float Opacity = 1f;

        /// <summary>Uniform scale for the title row (the left title and the optional right-aligned value,
        /// scaled together). Defaults to <c>1f</c> so a tooltip with no scaled lines renders unchanged. The
        /// body lines carry their own per-line <see cref="TooltipLine.Scale"/>.</summary>
        public float TitleScale = 1f;

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
            Rect bounds = ComputeBounds(_titleFont, _title, _titleRight, _bodyFont, _bodyFont, _lines, _anchor, Viewport, Metrics, ResolveMaxWidth(), TitleScale, AnchorMode);
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
        /// Unbounded width (the pre-existing behaviour): use the <see cref="MaxWidth"/> overloads to cap it.
        /// </summary>
        public static Rect ComputeBounds(ITextMeasurer titleFont, string title, ITextMeasurer bodyFont,
            IReadOnlyList<TooltipLine> lines, Vector2 anchor, Vector2 viewport, TooltipMetrics m) =>
            ComputeBounds(titleFont, title, "", bodyFont, bodyFont, lines, anchor, viewport, m, float.PositiveInfinity);

        /// <summary>
        /// Width-capped single-column overload: as the 7-arg overload, but a body line wider than
        /// <paramref name="maxWidth"/> (minus horizontal padding) word-wraps into extra lines so the bubble grows
        /// downward and its width stays &lt;= <paramref name="maxWidth"/>. <paramref name="maxWidth"/> &lt;= 0 or
        /// <see cref="float.PositiveInfinity"/> means unbounded (identical to the 7-arg overload).
        /// </summary>
        public static Rect ComputeBounds(ITextMeasurer titleFont, string title, ITextMeasurer bodyFont,
            IReadOnlyList<TooltipLine> lines, Vector2 anchor, Vector2 viewport, TooltipMetrics m, float maxWidth) =>
            ComputeBounds(titleFont, title, "", bodyFont, bodyFont, lines, anchor, viewport, m, maxWidth);

        /// <summary>
        /// Two-column overload: as above, but the title row is <paramref name="title"/> (left) plus
        /// <paramref name="titleRight"/> (right-aligned, measured with <paramref name="titleRightFont"/>), so the
        /// bubble widens to fit both with a <see cref="TooltipMetrics.TitleRightGap"/> between them. Pass
        /// <paramref name="titleRight"/> <c>""</c> for a single-column title (identical to the 7-arg overload).
        /// </summary>
        public static Rect ComputeBounds(ITextMeasurer titleFont, string title, string titleRight,
            ITextMeasurer titleRightFont, ITextMeasurer bodyFont,
            IReadOnlyList<TooltipLine> lines, Vector2 anchor, Vector2 viewport, TooltipMetrics m) =>
            ComputeBounds(titleFont, title, titleRight, titleRightFont, bodyFont, lines, anchor, viewport, m, float.PositiveInfinity);

        /// <summary>
        /// Width-capped two-column overload (the full layout): body lines wider than <paramref name="maxWidth"/>
        /// minus horizontal padding word-wrap via <paramref name="bodyFont"/> (break on spaces, hard-break a token
        /// longer than the budget), so the bubble grows in height and its width is capped at
        /// <paramref name="maxWidth"/>. The title row stays single-line: a two-column title (with
        /// <paramref name="titleRight"/>) keeps the bubble at least wide enough to hold it so the name never
        /// overlaps the right-aligned value (that floor can exceed the cap), while a one-column title just clips.
        /// <paramref name="maxWidth"/> &lt;= 0 or <see cref="float.PositiveInfinity"/> means unbounded.
        /// <paramref name="titleScale"/> (default <c>1f</c>) scales the title row only. Each body line's own
        /// <see cref="TooltipLine.Scale"/> rides through <paramref name="lines"/>. The shorter overloads above
        /// all keep an unscaled title (they never pass <paramref name="titleScale"/>), so this is the only
        /// overload a caller with a scaled title needs. <paramref name="anchorMode"/> (default
        /// <see cref="TooltipAnchorMode.Centered"/>) picks the horizontal placement, and is likewise only
        /// reachable here.
        /// </summary>
        public static Rect ComputeBounds(ITextMeasurer titleFont, string title, string titleRight,
            ITextMeasurer titleRightFont, ITextMeasurer bodyFont,
            IReadOnlyList<TooltipLine> lines, Vector2 anchor, Vector2 viewport, TooltipMetrics m, float maxWidth,
            float titleScale = 1f, TooltipAnchorMode anchorMode = TooltipAnchorMode.Centered) =>
            ComputeLayout(titleFont, title, titleRight, titleRightFont, bodyFont, lines, anchor, viewport, m,
                maxWidth, titleScale, anchorMode, out _);

        /// <summary>Compatibility projection of the shared layout's wrapped rows into text lines.</summary>
        internal static Rect ComputeBounds(ITextMeasurer titleFont, string title, string titleRight,
            ITextMeasurer titleRightFont, ITextMeasurer bodyFont,
            IReadOnlyList<TooltipLine> lines, Vector2 anchor, Vector2 viewport, TooltipMetrics m, float maxWidth,
            float titleScale, TooltipAnchorMode anchorMode, out List<TooltipLine> visual)
        {
            Rect bounds = ComputeLayout(titleFont, title, titleRight, titleRightFont, bodyFont, lines,
                anchor, viewport, m, maxWidth, titleScale, anchorMode, out TooltipBodyLayout body);
            visual = new List<TooltipLine>(body.Lines.Count);
            for (int i = 0; i < body.Lines.Count; i++) visual.Add(body.Lines[i].Line);
            return bounds;
        }

        /// <summary>The single layout pass for bubble bounds and the body rows drawn inside them.</summary>
        internal static Rect ComputeLayout(ITextMeasurer titleFont, string title, string titleRight,
            ITextMeasurer titleRightFont, ITextMeasurer bodyFont,
            IReadOnlyList<TooltipLine> lines, Vector2 anchor, Vector2 viewport, TooltipMetrics m, float maxWidth,
            float titleScale, TooltipAnchorMode anchorMode, out TooltipBodyLayout body)
        {
            bool bounded = maxWidth > 0f && !float.IsInfinity(maxWidth);
            body = TooltipBodyLayout.Build(titleFont, bodyFont, lines, m, ContentWidthCap(maxWidth, m));

            bool hasTitle = !string.IsNullOrEmpty(title);
            float titleRowW = hasTitle ? titleFont.Measure(title).X * titleScale : 0f;
            if (!string.IsNullOrEmpty(titleRight))
                titleRowW += m.TitleRightGap + titleRightFont.Measure(titleRight).X * titleScale;

            float contentW = MathF.Max(titleRowW, body.Width);
            float contentH = 0f;
            if (hasTitle) contentH += titleFont.LineHeight * titleScale + m.TitleGap;
            contentH = body.AccumulateHeight(contentH, m.LineSpacing);

            float w = contentW + m.PadX * 2f;
            float h = contentH + m.PadY * 2f;
            if (bounded)
            {
                // The cap governs the wrappable body. A two-column title row (left name + right-aligned value)
                // is single-line and un-wrappable, so the bubble must stay at least its width or the name would
                // overlap the value; that floor can exceed the cap. A one-column title has nothing to the right,
                // so it just clips at the cap.
                float cap = maxWidth;
                if (!string.IsNullOrEmpty(titleRight)) cap = MathF.Max(cap, titleRowW + m.PadX * 2f);
                w = MathF.Min(w, cap);
            }

            // Centered straddles the anchor (a widget's own centre). Offset puts the bubble's left edge beside it
            // (the cursor-style placement, where the anchor is a pointer the bubble must not sit on top of).
            float x = anchorMode == TooltipAnchorMode.Offset
                ? anchor.X + m.AnchorOffsetX
                : anchor.X - w * 0.5f;
            float y = anchor.Y - h - m.AnchorOffsetY;          // above the anchor
            if (y < m.TopMargin) y = anchor.Y + m.AnchorOffsetY; // flip below

            x = Math.Clamp(x, m.Margin, MathF.Max(m.Margin, viewport.X - w - m.Margin));
            y = Math.Clamp(y, m.TopMargin, MathF.Max(m.TopMargin, viewport.Y - h - m.Margin));
            return new Rect(x, y, w, h);
        }

        /// <summary>The body word-wrap budget (design px) for a given bubble-width cap: the cap minus horizontal
        /// padding, or <see cref="float.PositiveInfinity"/> when unbounded. One source so bounds and draw agree.</summary>
        static float ContentWidthCap(float maxWidth, TooltipMetrics m) =>
            (maxWidth > 0f && !float.IsInfinity(maxWidth)) ? MathF.Max(0f, maxWidth - m.PadX * 2f) : float.PositiveInfinity;

        /// <summary>The effective bubble-width cap (design px) from <see cref="MaxWidth"/> / <see cref="MaxWidthFraction"/>;
        /// <see cref="float.PositiveInfinity"/> when neither is set. The smaller of the two positive caps wins.</summary>
        float ResolveMaxWidth()
        {
            float max = float.PositiveInfinity;
            if (MaxWidth > 0f) max = MaxWidth;
            if (MaxWidthFraction > 0f && Viewport.X > 0f) max = MathF.Min(max, MaxWidthFraction * Viewport.X);
            return max;
        }
    }
}
