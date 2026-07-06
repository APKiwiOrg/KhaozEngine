using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>Row kind in a <see cref="PopupPanel"/>.</summary>
    public enum PopupRowType { Header, Stat, Spacer }

    /// <summary>
    /// One content row in a <see cref="PopupPanel"/>: a section header, a label/value stat, or a spacer. The
    /// record stores the RESOLVED display strings; the <see cref="LocalizedText"/> factories resolve against the
    /// ambient catalog at construction (like <see cref="TooltipLine.Of"/>), so rebuild the rows to pick up a
    /// runtime locale switch.
    /// </summary>
    public readonly record struct PopupRow(PopupRowType Type, string Label, string Value, Vector4 ValueColor)
    {
        /// <summary>Optional icon colour, drawn as a small filled square before the label (null = no icon).</summary>
        public Vector4? IconColor { get; init; }

        /// <summary>A section header from localized text (resolved now against the ambient catalog).</summary>
        public static PopupRow Header(LocalizedText text) => new(PopupRowType.Header, text.Resolve(), "", Vector4.One);

        /// <summary>A label/value stat from localized text (both resolved now against the ambient catalog), with an
        /// optional <paramref name="iconColor"/> swatch drawn before the label.</summary>
        public static PopupRow Stat(LocalizedText label, LocalizedText value, Vector4 valueColor, Vector4? iconColor = null)
            => new(PopupRowType.Stat, label.Resolve(), value.Resolve(), valueColor) { IconColor = iconColor };

        public static PopupRow Spacer() => new(PopupRowType.Spacer, "", "", Vector4.Zero);

        /// <summary>Obsolete: pass a <see cref="LocalizedText"/>. A raw string bypasses localization.</summary>
        [Obsolete("Pass a LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        public static PopupRow Header(string text) => new(PopupRowType.Header, text, "", Vector4.One);

        /// <summary>Obsolete: pass <see cref="LocalizedText"/>. A raw string bypasses localization.</summary>
        [Obsolete("Pass a LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        public static PopupRow Stat(string label, string value, Vector4 valueColor) => new(PopupRowType.Stat, label, value, valueColor);
    }

    /// <summary>
    /// A modal dialog: dimmed scrim, centered panel auto-sized to its rows (clamped between <see cref="MinHeight"/>
    /// and <see cref="MaxHeightFraction"/> of the viewport), a title bar, label/value content rows, and a footer
    /// with a dismiss button (plus an optional primary action). <see cref="Update(Pointer)"/> blocks the pointer over the
    /// panel so the screen beneath ignores clicks. Internal scroll lives in <see cref="ScrollablePanel"/>.
    /// </summary>
    public sealed class PopupPanel
    {
        readonly List<PopupRow> _rows = new();

        /// <summary>
        /// The design-space viewport the panel centers within. Defaults to <see cref="Vector2.Zero"/> ("unset");
        /// the caller must assign the real design size before use. Leaving it unset throws on layout (see
        /// <see cref="PanelRect"/>) so a forgotten assignment fails loudly instead of silently mis-positioning.
        /// </summary>
        public Vector2 Viewport = Vector2.Zero;

        /// <summary>The (lazily resolved) title text, drawn in the title bar. Defaults to empty.</summary>
        public LocalizedText TitleContent;

        /// <summary>Obsolete shim for the former string field. Setting <c>Title</c> stores a raw, non-localized value.</summary>
        [Obsolete("Use TitleContent (LocalizedText). Setting Title stores a raw, non-localized value.")]
        [LocalizationExempt]
        public string Title
        {
            get => TitleContent.Resolve();
            set => TitleContent = LocalizedText.Raw(value);
        }

        public float WidthFraction = 0.85f;
        public float MaxHeightFraction = 0.85f;
        public float MinHeight = 150f;
        public float TitleBarHeight = 36f;
        public float FooterHeight = 40f;
        public float ContentPadding = 12f;
        public float RowHeight = 24f;
        public float HeaderRowHeight = 28f;
        public float SpacerHeight = 12f;
        public float ScrimOpacity = 0.6f;

        public bool ShowPrimaryAction;
        public bool PrimaryActionEnabled = true;

        /// <summary>The (lazily resolved) dismiss-button text. Defaults to a raw "Close".</summary>
        public LocalizedText DismissContent = LocalizedText.Raw("Close");

        /// <summary>The (lazily resolved) primary-action-button text. Defaults to a raw "OK".</summary>
        public LocalizedText PrimaryActionContent = LocalizedText.Raw("OK");

        /// <summary>Obsolete shim for the former string field. Setting <c>DismissText</c> stores a raw, non-localized value.</summary>
        [Obsolete("Use DismissContent (LocalizedText). Setting DismissText stores a raw, non-localized value.")]
        [LocalizationExempt]
        public string DismissText
        {
            get => DismissContent.Resolve();
            set => DismissContent = LocalizedText.Raw(value);
        }

        /// <summary>Obsolete shim for the former string field. Setting <c>PrimaryActionText</c> stores a raw, non-localized value.</summary>
        [Obsolete("Use PrimaryActionContent (LocalizedText). Setting PrimaryActionText stores a raw, non-localized value.")]
        [LocalizationExempt]
        public string PrimaryActionText
        {
            get => PrimaryActionContent.Resolve();
            set => PrimaryActionContent = LocalizedText.Raw(value);
        }

        public bool WasPrimaryActionClicked { get; private set; }

        public Vector4 ScrimColor = new(0f, 0f, 0f, 1f);
        public Vector4 PanelColor = new(0.063f, 0.071f, 0.11f, 0.96f);
        public Vector4 PanelBorder = new(0.24f, 0.255f, 0.33f, 1f);
        public Vector4 TitleBarColor = new(0.086f, 0.098f, 0.149f, 1f);
        public Vector4 TitleColor = Vector4.One;
        public Vector4 HeaderColor = new(0.71f, 0.75f, 0.82f, 1f);
        public Vector4 LabelColor = new(0.67f, 0.69f, 0.73f, 1f);
        public Vector4 DismissColor = new(0.18f, 0.30f, 0.42f, 1f);
        public Vector4 PrimaryColor = new(0.20f, 0.44f, 0.30f, 1f);
        public Vector4 DisabledColor = new(0.14f, 0.14f, 0.17f, 1f);

        /// <summary>
        /// Modern-look knobs (rounded/shadow/gradient/glow) for the panel body and footer buttons; defaults to the
        /// flat <see cref="GuiStyle.Default"/> so the popup renders byte-identically to pre-7.8.0. The popup keeps
        /// its own colours; only the affordance knobs are read, and the full-screen scrim plus the title bar stay
        /// flat (the title bar's square corners overlap a rounded body's top corners by at most the radius). Set
        /// <c>Style = GuiStyle.Modern</c> to opt in.
        /// </summary>
        public GuiStyle Style = GuiStyle.Default;

        /// <summary>
        /// Uniform fade multiplied into every colour's alpha at draw time (1 = opaque). Lets a caller fade the whole
        /// popup in/out with a host transition. Default 1 is a no-op. Mirrors <see cref="Dropdown.Opacity"/>.
        /// </summary>
        public float Opacity = 1f;

        /// <summary>
        /// When true, a stat row with an empty <see cref="PopupRow.Value"/> wraps its label across the content width
        /// (growing the row's height to fit) instead of drawing a single clipped line. Needs <see cref="BodyFont"/>.
        /// Default false keeps every existing consumer byte-identical.
        /// </summary>
        public bool WrapLongLabels = false;

        /// <summary>Side length, in design units, of a <see cref="PopupRow.IconColor"/> swatch (and the 5-unit gap after it).</summary>
        public float IconSize = 10f;

        /// <summary>Design units the content scrolls per unit of wheel-scroll delta. Only bites when content overflows.</summary>
        public float ScrollWheelSpeed = 30f;

        float _scrollOffset;

        /// <summary>The current vertical scroll offset in design units (0 at the top). Clamped to the content overflow;
        /// always 0 when the content fits. Read-only: driven by wheel / drag-to-scroll in <see cref="Update(Pointer, float)"/>.</summary>
        public float ScrollOffset => _scrollOffset;

        public SpriteFont? TitleFont, BodyFont;

        public PopupPanel() { }

        /// <summary>Replace the content rows (resetting the scroll offset when the content actually changes). Call
        /// before <see cref="Update(Pointer)"/>/<see cref="Draw"/>. A no-op when the rows are value-equal to the current set,
        /// so calling it every frame does not reset an in-progress scroll.</summary>
        public void SetRows(IReadOnlyList<PopupRow> rows)
        {
            bool changed = _rows.Count != rows.Count;
            if (!changed)
                for (int i = 0; i < rows.Count; i++)
                    if (_rows[i] != rows[i]) { changed = true; break; }
            if (!changed) return;

            _rows.Clear();
            _rows.AddRange(rows);
            _scrollOffset = 0f;
        }

        const float IconGap = 5f;

        // The horizontal indent (icon swatch + gap) a row's label starts at, so wrap width and draw agree.
        float LabelIndent(in PopupRow row) => row.IconColor.HasValue ? IconSize + IconGap : 0f;

        float RowHeightOf(in PopupRow row, float contentWidth)
        {
            switch (row.Type)
            {
                case PopupRowType.Header: return HeaderRowHeight;
                case PopupRowType.Spacer: return SpacerHeight;
                default:
                    if (!WrapLongLabels || BodyFont == null || !string.IsNullOrEmpty(row.Value))
                        return RowHeight;
                    float labelWidth = MathF.Max(1f, contentWidth - LabelIndent(row));
                    return MathF.Max(RowHeight, TextLayout.MeasureWrappedHeight(BodyFont, row.Label, labelWidth));
            }
        }

        float ContentHeight(float contentWidth)
        {
            float h = 0f;
            foreach (var r in _rows)
                h += RowHeightOf(r, contentWidth);
            return h;
        }

        /// <summary>The centered, auto-sized panel rectangle.</summary>
        public Rect PanelRect()
        {
            if (Viewport == Vector2.Zero)
                throw new InvalidOperationException(
                    "PopupPanel.Viewport is unset (Vector2.Zero); assign the design viewport size before layout/draw.");
            float panelW = Viewport.X * WidthFraction;
            float maxH = Viewport.Y * MaxHeightFraction;
            float contentW = MathF.Max(1f, panelW - ContentPadding * 2f);
            float totalH = Math.Clamp(TitleBarHeight + ContentHeight(contentW) + FooterHeight + ContentPadding * 2f, MinHeight, maxH);
            float x = (Viewport.X - panelW) * 0.5f;
            float y = (Viewport.Y - totalH) * 0.4f;   // slightly above center
            return new Rect(x, y, panelW, totalH);
        }

        /// <summary>The content area between the title bar and the footer.</summary>
        public Rect ContentRect()
        {
            Rect p = PanelRect();
            float top = p.Y + TitleBarHeight + ContentPadding;
            float bottom = p.Bottom - FooterHeight - ContentPadding;
            return new Rect(p.X + ContentPadding, top, p.Width - ContentPadding * 2f, MathF.Max(0f, bottom - top));
        }

        const float BtnW = 130f, BtnH = 30f, BtnGap = 10f;

        // When both footer buttons share the row they shrink to fit a narrow panel: half the inner width less the gap,
        // capped at the fixed BtnW. A wide panel keeps the full 130 so single-and-wide layouts stay byte-identical.
        float TwoButtonWidth(Rect p) => MathF.Min(BtnW, (p.Width - ContentPadding * 2f - BtnGap) * 0.5f);

        /// <summary>The dismiss button rectangle (left button when a primary action is shown, else centered).</summary>
        public Rect DismissBounds()
        {
            Rect p = PanelRect();
            float by = p.Bottom - FooterHeight + (FooterHeight - BtnH) * 0.5f;
            if (!ShowPrimaryAction)
                return new Rect(p.X + (p.Width - BtnW) * 0.5f, by, BtnW, BtnH);
            float w = TwoButtonWidth(p);
            float total = w * 2f + BtnGap;
            float bx = p.X + (p.Width - total) * 0.5f;
            return new Rect(bx, by, w, BtnH);
        }

        /// <summary>The primary-action button rectangle (right of dismiss); empty when not shown.</summary>
        public Rect PrimaryBounds()
        {
            if (!ShowPrimaryAction) return new Rect(0, 0, 0, 0);
            Rect d = DismissBounds();
            return new Rect(d.Right + BtnGap, d.Y, d.Width, BtnH);
        }

        /// <summary>Reserve the panel region, hit-test the footer buttons. Returns true if dismiss was tapped.</summary>
        public bool Update(Pointer pointer) => Update(pointer, 0f);

        /// <summary>
        /// Reserve the panel region, scroll the content on wheel (<paramref name="wheelDelta"/>, e.g.
        /// <c>InputState.ScrollDelta</c>) and drag-to-scroll, and hit-test the footer buttons. Returns true if dismiss
        /// was tapped. Scrolling only bites when the content overflows the content area; otherwise this is the same as
        /// the wheel-less overload.
        /// </summary>
        public bool Update(Pointer pointer, float wheelDelta)
        {
            WasPrimaryActionClicked = false;
            pointer.BlockRegion(PanelRect());

            Rect content = ContentRect();
            if (wheelDelta != 0f && pointer.IsPointerIn(content))
            {
                _scrollOffset -= wheelDelta * ScrollWheelSpeed;
                ClampScroll(content);
            }
            Vector2 drag = pointer.GetDragDelta(content);
            if (drag.Y != 0f)
            {
                _scrollOffset -= drag.Y;
                ClampScroll(content);
            }

            if (ShowPrimaryAction && PrimaryActionEnabled && pointer.IsTapIn(PrimaryBounds()))
                WasPrimaryActionClicked = true;

            return pointer.IsTapIn(DismissBounds());
        }

        void ClampScroll(Rect content)
        {
            float maxScroll = MathF.Max(0f, ContentHeight(content.Width) - content.Height);
            _scrollOffset = Math.Clamp(_scrollOffset, 0f, maxScroll);
        }

        /// <summary>Draw the full popup. <paramref name="white"/> is a 1x1 white texture.</summary>
        public void Draw(SpriteBatch batch, Texture2D white, Pointer pointer)
        {
            Rect p = PanelRect();
            GuiDraw.Fill(batch, white, new Rect(0, 0, Viewport.X, Viewport.Y),
                new Vector4(ScrimColor.X, ScrimColor.Y, ScrimColor.Z, ScrimOpacity * Opacity));
            GuiDraw.FillStyled(batch, white, p, Style with { BorderThickness = 1f },
                GuiDraw.WithOpacity(PanelColor, Opacity), GuiDraw.WithOpacity(PanelBorder, Opacity));
            GuiDraw.Fill(batch, white, new Rect(p.X, p.Y, p.Width, TitleBarHeight), GuiDraw.WithOpacity(TitleBarColor, Opacity));

            if (TitleFont != null)
                TextLayout.DrawAligned(batch, TitleFont, TitleContent.Resolve(), p.X, p.Width,
                    p.Y + (TitleBarHeight - TitleFont.LineHeight) * 0.5f, TextAlign.Center, (Color)GuiDraw.WithOpacity(TitleColor, Opacity));

            if (BodyFont != null)
            {
                Rect content = ContentRect();
                batch.SetScissor(content);
                DrawRows(batch, white, content);
                batch.ClearScissor();
            }

            DrawButton(batch, white, DismissBounds(), DismissContent.Resolve(), DismissColor, true, pointer);
            if (ShowPrimaryAction)
                DrawButton(batch, white, PrimaryBounds(), PrimaryActionContent.Resolve(), PrimaryColor, PrimaryActionEnabled, pointer);
        }

        void DrawRows(SpriteBatch batch, Texture2D white, Rect c)
        {
            float y = c.Y - _scrollOffset;
            foreach (var r in _rows)
            {
                float rowH = RowHeightOf(r, c.Width);
                switch (r.Type)
                {
                    case PopupRowType.Header:
                        TextLayout.DrawAligned(batch, BodyFont!, r.Label, c.X, c.Width, y + 6f, TextAlign.Left,
                            (Color)GuiDraw.WithOpacity(HeaderColor, Opacity));
                        break;
                    case PopupRowType.Stat:
                        float labelX = c.X;
                        if (r.IconColor.HasValue)
                        {
                            var swatch = new Rect(c.X, y + (rowH - IconSize) * 0.5f, IconSize, IconSize);
                            GuiDraw.Fill(batch, white, swatch, GuiDraw.WithOpacity(r.IconColor.Value, Opacity));
                            GuiDraw.Border(batch, white, swatch, 1f, GuiDraw.WithOpacity(PanelBorder, Opacity));
                            labelX = c.X + IconSize + IconGap;
                        }
                        float labelW = c.Right - labelX;

                        if (WrapLongLabels && string.IsNullOrEmpty(r.Value))
                        {
                            float textH = TextLayout.MeasureWrappedHeight(BodyFont!, r.Label, labelW);
                            float ly = y + MathF.Max(0f, (rowH - textH) * 0.5f);
                            TextLayout.DrawWrapped(batch, BodyFont!, r.Label, new Vector2(labelX, ly), labelW,
                                TextAlign.Left, (Color)GuiDraw.WithOpacity(LabelColor, Opacity));
                        }
                        else
                        {
                            float ty = y + (RowHeight - BodyFont!.LineHeight) * 0.5f;
                            TextLayout.DrawAligned(batch, BodyFont!, r.Label, labelX, labelW, ty, TextAlign.Left,
                                (Color)GuiDraw.WithOpacity(LabelColor, Opacity));
                            TextLayout.DrawAligned(batch, BodyFont!, r.Value, c.X, c.Width, ty, TextAlign.Right,
                                (Color)GuiDraw.WithOpacity(r.ValueColor, Opacity));
                        }
                        break;
                    case PopupRowType.Spacer:
                        break;
                }
                y += rowH;
            }
        }

        // A button's per-state palette built from its semantic fill (Dismiss blue / Primary green). Hover/press
        // derive from the fill (hover keeps the old color*1.3 brighten); label stays white as before. Starts from
        // the popup's Style so the footer buttons inherit its modern affordances (rounded/shadow/gradient/glow);
        // when Style is the flat default this is byte-identical to the old hand-built palette.
        GuiStyle ButtonStyle(Vector4 fill)
        {
            var s = Style;
            s.Fill = GuiDraw.WithOpacity(fill, Opacity);
            s.Hover = GuiDraw.WithOpacity(fill * 1.3f, Opacity);
            s.Press = GuiDraw.WithOpacity(fill * 1.15f, Opacity);
            s.Border = GuiDraw.WithOpacity(PanelBorder, Opacity);
            s.Text = GuiDraw.WithOpacity(Vector4.One, Opacity);
            s.DisabledFill = GuiDraw.WithOpacity(DisabledColor, Opacity);
            s.DisabledText = GuiDraw.WithOpacity(Vector4.One, Opacity);
            s.SelectedFill = GuiDraw.WithOpacity(fill, Opacity);
            s.SelectedBorder = GuiDraw.WithOpacity(PanelBorder, Opacity);
            s.BorderThickness = 1f;
            return s;
        }

        void DrawButton(SpriteBatch batch, Texture2D white, Rect r, string text, Vector4 color, bool enabled, Pointer pointer)
        {
            // Route through the shared GuiDraw.DrawButton so popups inherit GuiStyle's state-priority and the
            // press-origin affordance (IsPressingIn) instead of hand-rolling fill+hover+label here.
            if (BodyFont == null)
            {
                GuiDraw.Fill(batch, white, r, GuiDraw.WithOpacity(enabled ? color : DisabledColor, Opacity));
                return;
            }
            GuiDraw.DrawButton(batch, white, BodyFont, r, LocalizedText.Raw(text), ButtonStyle(color), enabled,
                selected: false, pointer.IsHoveringIn(r), pointer.IsPressingIn(r));
        }
    }
}
