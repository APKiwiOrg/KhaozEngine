using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

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
        /// Caller-ordered localized label parts, or null for the legacy single <see cref="Label"/> string.
        /// </summary>
        public IReadOnlyList<LabelSegment>? LabelSegments { get; init; }

        /// <summary>
        /// Build an entry from localized text, resolved now against the ambient catalog.
        /// <paramref name="rightDetail"/> defaults to <c>default(LocalizedText)</c>, which resolves to the empty
        /// string, so a row with no right-hand detail needs no extra ceremony.
        /// </summary>
        public static ContextMenuEntry Of(LocalizedText label, LocalizedText rightDetail = default,
            Vector4? labelColor = null, Vector4? detailColor = null, long tag = 0, bool enabled = true) =>
            new(label.Resolve() ?? "", rightDetail.Resolve() ?? "", labelColor, detailColor, tag, enabled);

        /// <summary>
        /// Builds an entry whose left label is measured and drawn as caller-ordered localized segments.
        /// Segments are copied so later mutations of the source list cannot change an open menu.
        /// </summary>
        public static ContextMenuEntry Segmented(
            IReadOnlyList<LabelSegment> labelSegments,
            LocalizedText rightDetail = default,
            Vector4? labelColor = null,
            Vector4? detailColor = null,
            long tag = 0,
            bool enabled = true)
        {
            ArgumentNullException.ThrowIfNull(labelSegments);
            var copy = new LabelSegment[labelSegments.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = labelSegments[i];
            return new ContextMenuEntry("", rightDetail.Resolve() ?? "", labelColor, detailColor, tag, enabled)
            {
                LabelSegments = copy,
            };
        }
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
    public sealed partial class ContextMenu
    {
        readonly ITextMeasurer _titleMeasure, _bodyMeasure;
        readonly SpriteFont? _titleFont, _bodyFont;

        readonly List<ContextMenuEntry> _entries = new();
        string _title = "";
        Vector2 _point;
        bool _openedThisFrame;
        bool _openGestureLatch;

        /// <summary>Spacing knobs used by the layout and the draw. Defaults to <see cref="ContextMenuMetrics.Default"/>.</summary>
        public ContextMenuMetrics Metrics = ContextMenuMetrics.Default;

        /// <summary>Build a menu that measures and draws its title band with <paramref name="titleFont"/> and its entry rows with <paramref name="bodyFont"/>.</summary>
        public ContextMenu(SpriteFont titleFont, SpriteFont bodyFont)
        {
            _titleFont = titleFont; _bodyFont = bodyFont;
            _titleMeasure = titleFont; _bodyMeasure = bodyFont;
        }

        /// <summary>
        /// Measure-only build (the <see cref="ToastView"/> precedent): layout and interaction run off plain
        /// <see cref="ITextMeasurer"/>s, so a headless test drives <see cref="Open"/> / <see cref="Update(Pointer)"/>
        /// without a GPU device or a baked font. <see cref="Draw"/> throws on a menu built this way, because
        /// there is no <see cref="SpriteFont"/> to render glyphs with.
        /// </summary>
        public ContextMenu(ITextMeasurer titleFont, ITextMeasurer bodyFont)
        {
            _titleMeasure = titleFont; _bodyMeasure = bodyFont;
        }

        /// <summary>True while the menu is showing. Set by <see cref="Open"/>, cleared by <see cref="Close"/>, a selection or a dismissal.</summary>
        public bool IsOpen { get; private set; }

        /// <summary>
        /// The design-space viewport the menu clamps within. Defaults to <see cref="Vector2.Zero"/> ("unset"):
        /// assign the real design size before updating or drawing an open menu. An open menu with this unset
        /// throws in <see cref="Draw"/> (the <see cref="Tooltip"/> precedent) so a forgotten assignment fails
        /// loudly instead of silently pinning the menu into the top-left corner.
        /// </summary>
        public Vector2 Viewport = Vector2.Zero;

        /// <summary>
        /// True only on the frame a row was selected (the <see cref="Dropdown.WasChanged"/> precedent), which is
        /// also the frame <see cref="Update(Pointer)"/> returned <c>true</c>. Every flag in this group
        /// (<see cref="SelectedTag"/>, <see cref="SelectedIndex"/>, <see cref="WasDismissed"/>,
        /// <see cref="DismissPress"/>) is cleared at the top of the next <see cref="Update(Pointer)"/>, closed
        /// menu included, so read them on the frame they fire rather than stashing the object and reading later.
        /// </summary>
        public bool WasSelected { get; private set; }

        /// <summary>The selected row's <see cref="ContextMenuEntry.Tag"/> on the selection frame, else <c>0</c>.</summary>
        public long SelectedTag { get; private set; }

        /// <summary>The selected row's index on the selection frame, else <c>-1</c>.</summary>
        public int SelectedIndex { get; private set; } = -1;

        /// <summary>True only on the frame the menu was dismissed without a selection (a release outside it, or menu-cancel).</summary>
        public bool WasDismissed { get; private set; }

        /// <summary>
        /// Where the dismissing gesture RELEASED, when the dismissal came from a release outside the menu, else
        /// <c>null</c> (a menu-cancel key has no position). This is the release position rather than the press
        /// origin, deliberately: <see cref="Pointer.IsReleasedOutside"/> keys on the release, so a press that
        /// began inside the menu and dragged out reports where the cursor actually ended up. A caller that
        /// reopens on the dismissing gesture anchors the new menu here, under the cursor, not at a stale origin.
        /// The pointer dismissal is a LEFT release outside and nothing else: a right press outside an open menu
        /// does not touch it, so a caller wanting right-click-to-reopen watches the right button itself and
        /// calls <see cref="Open"/> again at the new point.
        /// </summary>
        public Vector2? DismissPress { get; private set; }

        /// <summary>
        /// The row under the pointer, or <c>-1</c> for none. Never a disabled row, so the hover fill and any
        /// caller-side hover affordance agree with what selection will actually accept. <c>-1</c> while closed.
        /// </summary>
        public int HoverIndex { get; private set; } = -1;

        /// <summary>Menu fill.</summary>
        public Vector4 Background = GuiTheme.Default.Background;
        /// <summary>Menu border, and the 1px separator under the title band.</summary>
        public Vector4 Border = GuiTheme.Default.Border;
        /// <summary>Title text in the header band.</summary>
        public Vector4 TitleColor = GuiTheme.Default.TextMuted;
        /// <summary>Row label text, unless the entry carries its own <see cref="ContextMenuEntry.LabelColor"/>.</summary>
        public Vector4 TextColor = GuiTheme.Default.Text;
        /// <summary>Right-aligned row detail text, unless the entry carries its own <see cref="ContextMenuEntry.DetailColor"/>.</summary>
        public Vector4 DetailColor = GuiTheme.Default.TextMuted;
        /// <summary>Row fill under <see cref="HoverIndex"/>.</summary>
        public Vector4 HoverColor = GuiTheme.Default.SurfaceHover;
        /// <summary>
        /// Label and detail text on a disabled row (muted text at half alpha), which overrides the entry's own
        /// colours: a disabled row must read as disabled whatever the caller tinted it.
        /// </summary>
        public Vector4 DisabledColor = new(GuiTheme.Default.TextMuted.X, GuiTheme.Default.TextMuted.Y,
            GuiTheme.Default.TextMuted.Z, GuiTheme.Default.TextMuted.W * 0.5f);

        /// <summary>
        /// Show a menu with this title and these entries anchored at <paramref name="screenPoint"/> (design
        /// pixels). Reopening while already open simply replaces the content and the point, and clears every
        /// frame flag, so a right press that lands on a new target swaps the menu in one call. A null or empty
        /// <paramref name="entries"/> opens a title-only menu rather than throwing.
        /// <para>
        /// Latches the whole OPENING GESTURE (the <see cref="Tooltip"/> precedent, widened from one frame to one
        /// gesture), so the gesture that opened the menu can never dismiss it and can never select a row in it.
        /// A caller opening on a LEFT PRESS or a LEFT RELEASE hands the first <see cref="Update(Pointer)"/> a
        /// frame that already carries that gesture's edge, and when the clamp in <see cref="ComputeBounds"/>
        /// moves the menu the gesture reads either as a release outside the menu it just opened (a dismissal) or
        /// as a tap on a row the menu dropped under the cursor (a selection). Neither is deliberate: a press that
        /// began before the menu existed cannot be an act on it. The latch disarms on the first
        /// <see cref="Pointer.IsJustPressed"/> landing on a frame AFTER the opening one, and that press is then
        /// read normally from its own edge, so the opening gesture can neither dismiss the menu nor select a row
        /// in it. Menu-cancel via <see cref="Update(InputManager, PlayerIndex?)"/> stays live throughout,
        /// since the keyboard was not the opening gesture.
        /// </para>
        /// </summary>
        public void Open(LocalizedText title, IReadOnlyList<ContextMenuEntry> entries, Vector2 screenPoint)
        {
            _title = title.Resolve() ?? "";
            _entries.Clear();
            if (entries != null)
                for (int i = 0; i < entries.Count; i++) _entries.Add(entries[i]);
            _point = screenPoint;
            IsOpen = true;
            HoverIndex = -1;
            _openedThisFrame = true;
            _openGestureLatch = true;
            ClearFrameFlags();
        }

        /// <summary>Hide the menu. Leaves this frame's flags alone, so a caller closing in response to a selection still reads it.</summary>
        public void Close()
        {
            IsOpen = false;
            HoverIndex = -1;
        }

        void ClearFrameFlags()
        {
            WasSelected = false;
            SelectedIndex = -1;
            SelectedTag = 0;
            WasDismissed = false;
            DismissPress = null;
        }

        /// <summary>
        /// Drive one frame of the open menu (a no-op beyond clearing the frame flags while closed). Reserves the
        /// menu's bounds through <see cref="Pointer.BlockRegion"/> (the <see cref="Dropdown"/> precedent) so the
        /// world beneath cannot be clicked through it, tracks <see cref="HoverIndex"/>, selects on a tap inside
        /// an ENABLED row (setting <see cref="WasSelected"/> / <see cref="SelectedTag"/> /
        /// <see cref="SelectedIndex"/> and closing), and dismisses on a release outside the bounds (setting
        /// <see cref="WasDismissed"/> and <see cref="DismissPress"/>). BOTH of those are suppressed while the
        /// opening-gesture latch from <see cref="Open"/> is armed, which lasts until a press edge lands on a
        /// frame after the opening one. <see cref="HoverIndex"/> and the <see cref="Pointer.BlockRegion"/>
        /// reservation are computed either way, so a latched frame still highlights and blocks exactly like any
        /// other open frame. A tap inside the menu that hits the title band or a disabled row does nothing and
        /// leaves the menu open. Returns <see cref="WasSelected"/>.
        /// </summary>
        public bool Update(Pointer pointer)
        {
            ClearFrameFlags();
            if (!IsOpen) { HoverIndex = -1; _openedThisFrame = false; _openGestureLatch = false; return false; }

            bool openingFrame = _openedThisFrame;
            _openedThisFrame = false;
            // A press edge on a LATER frame began when the menu already existed, so it is the user's first
            // deliberate gesture at it: disarm, and read that same press normally from its own edge below. A
            // press edge on the opening frame itself belongs to the gesture that opened the menu, and leaving
            // the latch armed there is what carries it across a press-open gesture's release a frame later.
            if (_openGestureLatch && !openingFrame && pointer.IsJustPressed) _openGestureLatch = false;

            Rect bounds = Bounds();
            pointer.BlockRegion(bounds);

            HoverIndex = -1;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (!_entries[i].Enabled) continue;
                if (!pointer.IsPointerIn(RowBounds(bounds, _titleMeasure, _bodyMeasure, i, Metrics))) continue;
                HoverIndex = i;
                break;
            }

            // Neither query below can tell an opening gesture from a deliberate one on its own. IsTapIn carries
            // a press-origin invariant but no notion of when the menu appeared, so a menu the clamp drops under
            // a held cursor reads the pre-open press as a row tap. IsReleasedOutside is a pure edge plus
            // position, with no origin invariant at all. Pointer.ConsumeGesture is the caller-side answer and
            // gates the tap queries only, so the menu carries its own latch and gates both paths here.
            if (_openGestureLatch) return false;

            for (int i = 0; i < _entries.Count; i++)
            {
                ContextMenuEntry e = _entries[i];
                if (!e.Enabled) continue;
                if (!pointer.IsTapIn(RowBounds(bounds, _titleMeasure, _bodyMeasure, i, Metrics))) continue;
                WasSelected = true;
                SelectedIndex = i;
                SelectedTag = e.Tag;
                Close();
                return true;
            }

            if (pointer.IsReleasedOutside(bounds))
            {
                WasDismissed = true;
                DismissPress = pointer.Position;
                Close();
            }
            return false;
        }

        /// <summary>
        /// <see cref="Update(Pointer)"/> plus menu-cancel (Escape / gamepad B / Back) dismissal, mirroring
        /// <see cref="Dropdown.Update(InputManager, bool, PlayerIndex?)"/>. A cancel sets
        /// <see cref="WasDismissed"/> with a null <see cref="DismissPress"/>, since there is no outside press to
        /// reopen from. Cancel is NOT gated by the opening-gesture latch, deliberately: the keyboard was never
        /// the gesture that opened the menu, so Escape closes it on the very first frame. <paramref name="player"/>
        /// scopes gamepad input (null = any player).
        /// </summary>
        public bool Update(InputManager input, PlayerIndex? player = null)
        {
            bool selected = Update(input.Pointer);
            if (IsOpen && input.IsMenuCancel(player, out _))
            {
                WasDismissed = true;
                DismissPress = null;
                Close();
            }
            return selected;
        }

        /// <summary>
        /// Draw the open menu (a no-op while closed). <paramref name="white"/> is a 1x1 white texture. The title
        /// band is always painted, empty title included, so the header never reads as dead space: title text at
        /// <see cref="TitleColor"/> plus a 1px separator in <see cref="Border"/> across the bottom of the band.
        /// Rows walk the same <see cref="RowBounds"/> geometry the hit-testing does.
        /// </summary>
        public void Draw(SpriteBatch batch, Texture2D white)
        {
            if (!IsOpen) return;
            if (Viewport == Vector2.Zero)
                throw new InvalidOperationException(
                    "ContextMenu.Viewport is unset (Vector2.Zero). Assign the design viewport size before draw.");
            if (_titleFont == null || _bodyFont == null)
                throw new InvalidOperationException(
                    "ContextMenu was built measure-only, via the ITextMeasurer constructor. Build it with SpriteFonts to draw.");

            Rect b = Bounds();
            GuiDraw.Fill(batch, white, b, Background);
            GuiDraw.Border(batch, white, b, 1f, Border);

            float textX = MathF.Floor(b.X + Metrics.PadX);
            if (!string.IsNullOrEmpty(_title))
                batch.DrawString(_titleFont, _title, new Vector2(textX, MathF.Floor(b.Y + Metrics.RowPadY)), (Color)TitleColor);
            // Centred in the TitleGap band under the title text, above where the first row starts.
            float sepY = MathF.Floor(b.Y + TitleBandHeight(_titleMeasure, Metrics) - Metrics.TitleGap * 0.5f);
            GuiDraw.Fill(batch, white, new Rect(textX, sepY, MathF.Max(0f, b.Width - Metrics.PadX * 2f), 1f), Border);

            for (int i = 0; i < _entries.Count; i++)
            {
                ContextMenuEntry e = _entries[i];
                Rect r = RowBounds(b, _titleMeasure, _bodyMeasure, i, Metrics);
                if (e.Enabled && i == HoverIndex) GuiDraw.Fill(batch, white, r, HoverColor);

                Vector4 detail = e.Enabled ? e.DetailColor ?? DetailColor : DisabledColor;
                float y = MathF.Floor(r.Y + Metrics.RowPadY);
                LabelRun[] runs = LayoutLabel(e, _bodyFont, textX, TextColor, DisabledColor);
                for (int segment = 0; segment < runs.Length; segment++)
                {
                    LabelRun run = runs[segment];
                    batch.DrawString(_bodyFont, run.Text, new Vector2(run.X, y), (Color)run.Color);
                }
                if (!string.IsNullOrEmpty(e.RightDetail))
                {
                    float dw = _bodyFont.Measure(e.RightDetail).X;
                    batch.DrawString(_bodyFont, e.RightDetail,
                        new Vector2(MathF.Floor(r.Right - Metrics.PadX - dw), y), (Color)detail);
                }
            }
        }

        /// <summary>The current menu rect, from the live title, entries and anchor point. One layout source for hit-testing and drawing.</summary>
        Rect Bounds() => ComputeBounds(_titleMeasure, _title, _bodyMeasure, _entries, _point, Viewport, Metrics);

        /// <summary>
        /// Pure layout: the on-screen rect for a menu with this title and these entries opened at
        /// <paramref name="point"/>. The menu's top-left sits AT the point and opens down-right, clamped into
        /// <paramref name="viewport"/> by <see cref="ContextMenuMetrics.Margin"/> on all four sides. When the
        /// bottom would overflow the viewport the menu flips to sit with its BOTTOM at the point instead, which
        /// mirrors the <see cref="Tooltip"/> flip. The clamp runs LAST and wins over the flip, so a point too
        /// close to an edge for either placement yields a menu pinned inside the margin box that may cover the
        /// point rather than sit at it. A menu too big for that box cannot fit in it, so it pins to the left and
        /// top margins and overflows the right and bottom edges.
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
                float rowW = MeasureLabel(bodyFont, e);
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
