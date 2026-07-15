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
    /// An immediate-mode UI surface: author a HUD or menu with one call site per widget
    /// (<c>if (ui.Button(font, rect, "Play")) {...}</c>) inside a live render loop, instead of keeping
    /// retained widget instances alive across frames. Each frame, call <see cref="Begin"/> with the
    /// already-begun <see cref="SpriteBatch"/> and the design-space <see cref="Pointer"/>, then issue widget
    /// calls. Every widget reserves its rect for the press-origin click-through gate (<see cref="PointerCaptured"/>),
    /// reproducing the manual <c>Pointer.BlockRegion</c> bookkeeping. Drawing reuses the internal
    /// <see cref="GuiDraw"/> helpers; the caller owns <c>batch.Begin(viewport)</c>/<c>End()</c> so the surface
    /// composes with the design viewport for free. Pass a <c>null</c> batch to <see cref="Begin"/> for headless
    /// interaction tests (return values + capture still compute; nothing draws).
    /// <para>
    /// <b>Immediate vs retained:</b> use <see cref="GuiSurface"/> for HUDs/menus authored fresh each frame inside a
    /// <c>window.Run</c> loop - no instances to keep, one call site per widget, styled by <see cref="GuiStyle"/>.
    /// Use the retained widgets (<see cref="Button(KhaozEngine.Render2D.SpriteFont, KhaozEngine.Primitives.Rect, string)"/>, <see cref="Toggle"/>, <see cref="Slider(KhaozEngine.Primitives.Rect, float)"/>,
    /// <see cref="Dropdown"/>, <see cref="TextInput"/>, ...) when a control owns persistent state across frames
    /// (focus, drag, open/closed) or sits in a long-lived screen object: construct once, call <c>Update</c> then
    /// <c>Draw</c> each frame. Both paradigms reserve their rect on the <see cref="Pointer"/> for the same
    /// press-origin click-through gate, share the internal <see cref="GuiDraw"/> drawing, and read from
    /// <see cref="GuiStyle"/> (retained widgets with richer palettes expose their own override colors). Don't drive
    /// the same control through both in one frame.
    /// </para>
    /// </summary>
    public sealed class GuiSurface
    {
        readonly Texture2D _white;
        readonly List<Rect> _blocked = new();
        SpriteBatch? _batch;
        Pointer _pointer = new();
        Rect? _hoveredRect;       // the enabled button under the pointer THIS frame (last one wins); null = none
        Rect? _prevHoveredRect;   // last frame's hovered button, for hover-enter detection

        // Memoizes StatChip's "label  value" interpolation, keyed on (label, value) content: a HUD stat chip
        // typically redraws every frame with an unchanged value (health, ammo, gold sitting steady between
        // changes), so a steady chip turns into a dictionary lookup instead of a fresh string allocation every
        // draw. Capped and wholesale-cleared past the cap rather than LRU-evicted - this is a small, cheap
        // -to-rebuild cache of short strings, so a simple bound is enough to stop a counter ticking through many
        // distinct values over a long session from growing it without limit.
        internal const int StatChipTextCacheCapacity = 64;
        readonly Dictionary<(string Label, string Value), string> _statChipTextCache = new();

        /// <summary>The number of distinct (label, value) pairs currently memoized. Internal, for tests.</summary>
        internal int StatChipTextCacheCount => _statChipTextCache.Count;

        // Internal (not private) so the cache/format logic is directly unit-testable without a GPU-backed
        // SpriteFont - StatChip only reaches this when font is non-null, which needs a real device.
        internal string FormatStatChipText(string lbl, string val)
        {
            var key = (lbl, val);
            if (_statChipTextCache.TryGetValue(key, out string? cached)) return cached;

            string text = string.IsNullOrEmpty(val) ? lbl : $"{lbl}  {val}";
            if (_statChipTextCache.Count >= StatChipTextCacheCapacity) _statChipTextCache.Clear();
            _statChipTextCache[key] = text;
            return text;
        }

        /// <summary>The style applied to <see cref="Button(SpriteFont, Rect, string)"/> when no explicit style is passed.</summary>
        public GuiStyle Style { get; set; }

        /// <summary>The icon set resolved by <see cref="Icon"/>/<see cref="IconButton"/>/<see cref="StatChip(Rect, string, LocalizedText, LocalizedText, SpriteFont, GuiStyle, float)"/>; null = icons draw nothing.</summary>
        public IconAtlas? IconAtlas { get; set; }

        /// <param name="white">A 1x1 white texture for rectangle fills.</param>
        /// <param name="style">The default widget style; <see cref="GuiStyle.Default"/> if null.</param>
        public GuiSurface(Texture2D white, GuiStyle? style = null)
        {
            _white = white;
            Style = style ?? GuiStyle.Default;
        }

        /// <summary>
        /// Begin a UI frame: capture the already-begun <paramref name="batch"/> (<c>null</c> for headless tests,
        /// where return values and capture still compute but nothing draws) and the design-space
        /// <paramref name="pointer"/>, and clear the per-frame blocked region set so <see cref="PointerCaptured"/>
        /// reflects only this frame's widgets.
        /// </summary>
        public void Begin(SpriteBatch? batch, Pointer pointer)
        {
            _batch = batch;
            _pointer = pointer;
            _blocked.Clear();
            // Roll the hovered-widget tracking: this frame's hover accumulates as Buttons are issued; compare
            // against last frame's to detect hover-enter. Read IsHovering/HoverEntered AFTER all widgets are drawn.
            _prevHoveredRect = _hoveredRect;
            _hoveredRect = null;
        }

        /// <summary>Draw a solid-filled <paramref name="rect"/>; reserves it for click-through.</summary>
        public void Panel(Rect rect, Vector4 fill)
        {
            _blocked.Add(rect);
            if (_batch is null) return;
            GuiDraw.Fill(_batch, _white, rect, fill);
        }

        /// <summary>Draw a filled <paramref name="rect"/> with an outline; reserves it for click-through.</summary>
        public void Panel(Rect rect, Vector4 fill, Vector4 border, float borderThickness = 1.5f)
        {
            _blocked.Add(rect);
            if (_batch is null) return;
            GuiDraw.Fill(_batch, _white, rect, fill);
            GuiDraw.Border(_batch, _white, rect, borderThickness, border);
        }

        /// <summary>Draw a panel honouring the full <paramref name="style"/> (rounded/shadow/gradient); reserves it for click-through.</summary>
        public void Panel(Rect rect, in GuiStyle style)
        {
            _blocked.Add(rect);
            if (_batch is null) return;
            GuiDraw.FillStyled(_batch, _white, rect, style, style.Fill, style.Border);
        }

        /// <summary>
        /// Draw icon <paramref name="id"/> into <paramref name="rect"/>, tinted by <paramref name="tint"/>, via the
        /// shared batched-quad path. No-op when no <see cref="IconAtlas"/> is set or the id is unknown. Decoration:
        /// does not reserve a rect (compose inside a button/chip to reserve).
        /// </summary>
        public void Icon(Rect rect, string id, Vector4 tint)
        {
            if (_batch is null || IconAtlas is null) return;
            if (!IconAtlas.TryGet(id, out var tex, out var uv)) return;
            _batch.Draw(tex, new Vector4(rect.X, rect.Y, rect.Width, rect.Height), uv, (Color)tint);
        }

        /// <summary>Draw a plain filled colour chip; reserves it for click-through.</summary>
        public void Swatch(Rect rect, Vector4 color)
        {
            _blocked.Add(rect);
            if (_batch is null) return;
            GuiDraw.Fill(_batch, _white, rect, color);
        }

        /// <summary>Draw <paramref name="text"/> at <paramref name="pos"/> (top-left), uniformly scaled by
        /// <paramref name="scale"/> about that corner (defaults to 1). Does not reserve any rect.</summary>
        public void Label(SpriteFont font, LocalizedText text, Vector2 pos, Vector4 color, float scale = 1f)
        {
            if (_batch is null) return;
            _batch.DrawString(font, text.Resolve(), pos, (Color)color, scale);
        }

        /// <summary>Obsolete: pass a <see cref="LocalizedText"/>. A raw string bypasses localization.</summary>
        [Obsolete("Pass a LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public void Label(SpriteFont font, string text, Vector2 pos, Vector4 color) =>
            Label(font, LocalizedText.Raw(text), pos, color);

        /// <summary>
        /// Draw <paramref name="text"/> aligned within <paramref name="rect"/> horizontally per
        /// <paramref name="align"/> and vertically centered, uniformly scaled by <paramref name="scale"/>
        /// (defaults to 1). The measured text scales with it so the alignment stays correct. Does not reserve any rect.
        /// </summary>
        public void Label(SpriteFont font, Rect rect, LocalizedText text, Vector4 color, GuiAlign align = GuiAlign.Center, float scale = 1f)
        {
            if (_batch is null) return;
            string s = text.Resolve();
            Vector2 pos = GuiDraw.AlignedTextPos(rect, font.Measure(s), font.LineHeight, align, scale, pad: 6f);
            _batch.DrawString(font, s, pos, (Color)color, scale);
        }

        /// <summary>Obsolete: pass a <see cref="LocalizedText"/>. A raw string bypasses localization.</summary>
        [Obsolete("Pass a LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public void Label(SpriteFont font, Rect rect, string text, Vector4 color, GuiAlign align = GuiAlign.Center) =>
            Label(font, rect, LocalizedText.Raw(text), color, align);

        /// <summary>A button with the surface's default <see cref="Style"/>. Returns true on a valid press-origin tap.</summary>
        public bool Button(SpriteFont font, Rect rect, LocalizedText label) =>
            Button(font, rect, label, Style);

        /// <summary>Obsolete: pass a <see cref="LocalizedText"/>. A raw string bypasses localization.</summary>
        [Obsolete("Pass a LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public bool Button(SpriteFont font, Rect rect, string label) =>
            Button(font, rect, LocalizedText.Raw(label), Style);

        /// <summary>
        /// A button with hover/press/disabled/selected visuals. Returns true on the release frame of a tap whose
        /// press began inside <paramref name="rect"/> (the <see cref="Pointer.IsTapIn"/> invariant), only when
        /// <paramref name="enabled"/>. Always reserves its rect for click-through (even disabled). The
        /// <paramref name="scale"/> scales the label only (defaults to 1); the rect and hit-test are unchanged.
        /// </summary>
        public bool Button(SpriteFont font, Rect rect, LocalizedText label, GuiStyle style, bool enabled = true, bool selected = false, float scale = 1f)
        {
            _blocked.Add(rect);

            Pointer p = _pointer;
            bool clicked = enabled && p.IsTapIn(rect);

            // Track hover for enabled buttons only (a disabled button shows no hover affordance, so it should not
            // drive hover feedback). Computed before the headless early-return so hover state is testable.
            bool hovering = enabled && p.IsHoveringIn(rect);
            if (hovering) _hoveredRect = rect;

            if (_batch is null) return clicked;

            bool pressing = p.IsPressingIn(rect);
            GuiDraw.DrawButton(_batch, _white, font, rect, label, style, enabled, selected, hovering, pressing, scale);

            return clicked;
        }

        /// <summary>Obsolete: pass a <see cref="LocalizedText"/>. A raw string bypasses localization.</summary>
        [Obsolete("Pass a LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public bool Button(SpriteFont font, Rect rect, string label, GuiStyle style, bool enabled = true, bool selected = false) =>
            Button(font, rect, LocalizedText.Raw(label), style, enabled, selected);

        /// <summary>
        /// An icon-only button (icon centred in a styled panel, tinted by the text colour, hover glow from the
        /// style). Returns true on a valid press-origin tap; always reserves its rect. Mirrors <see cref="Button(SpriteFont, Rect, string, GuiStyle, bool, bool)"/>.
        /// </summary>
        public bool IconButton(Rect rect, string iconId, GuiStyle style, bool enabled = true, bool selected = false)
        {
            _blocked.Add(rect);
            Pointer p = _pointer;
            bool clicked = enabled && p.IsTapIn(rect);
            bool hovering = enabled && p.IsHoveringIn(rect);
            if (hovering) _hoveredRect = rect;
            if (_batch is null) return clicked;

            bool pressing = p.IsPressingIn(rect);
            Vector4 fill = !enabled ? style.DisabledFill
                : selected ? style.SelectedFill
                : pressing ? style.Press
                : hovering ? style.Hover
                : style.Fill;
            Vector4 border = selected ? style.SelectedBorder : style.Border;
            Vector4 iconTint = enabled ? style.Text : style.DisabledText;

            if (hovering) GuiDraw.HoverGlow(_batch, _white, rect, style);
            GuiDraw.FillStyled(_batch, _white, rect, style, fill, border);

            float side = System.MathF.Min(rect.Width, rect.Height) * 0.6f;
            var iconRect = new Rect(rect.X + (rect.Width - side) * 0.5f, rect.Y + (rect.Height - side) * 0.5f, side, side);
            Icon(iconRect, iconId, iconTint);
            return clicked;
        }

        /// <summary>
        /// A non-interactive "stat chip": a styled rounded panel with an icon at the left and a label/value to its
        /// right. Reserves its rect for click-through (like <see cref="Panel(Rect, Vector4)"/>). A null
        /// <paramref name="font"/> draws panel + icon only (headless-safe).
        /// </summary>
        public void StatChip(Rect rect, string iconId, LocalizedText label, LocalizedText value, SpriteFont font, GuiStyle style, float scale = 1f)
        {
            _blocked.Add(rect);
            if (_batch is null) return;

            GuiDraw.FillStyled(_batch, _white, rect, style, style.Fill, style.Border);

            float pad = rect.Height * 0.18f;
            float iconSide = rect.Height - pad * 2f;
            var iconRect = new Rect(rect.X + pad, rect.Y + pad, iconSide, iconSide);
            Icon(iconRect, iconId, style.Text);

            if (font is null) return;
            float textX = iconRect.Right + pad;
            float ty = rect.Y + (rect.Height - font.LineHeight * scale) * 0.5f;
            string lbl = label.Resolve();
            string val = value.Resolve();
            string text = FormatStatChipText(lbl, val);
            _batch.DrawString(font, text, new Vector2(textX, ty), (Color)style.Text, scale);
        }

        /// <summary>Obsolete: pass <see cref="LocalizedText"/> for the label/value. A raw string bypasses localization.</summary>
        [Obsolete("Pass LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public void StatChip(Rect rect, string iconId, string label, string value, SpriteFont font, GuiStyle style) =>
            StatChip(rect, iconId, LocalizedText.Raw(label), LocalizedText.Raw(value), font, style);

        /// <summary>A horizontal slider using the surface's default <see cref="Style"/>. Returns the value in [0,1].</summary>
        public float Slider(Rect rect, float value) => Slider(rect, value, Style);

        /// <summary>
        /// An immediate-mode horizontal slider. Returns the (possibly updated) value in [0,1]. While the pointer is
        /// pressed with its press-origin inside <paramref name="rect"/> (the same press-origin invariant as
        /// <see cref="Button(SpriteFont, Rect, string)"/>, via <see cref="Pointer.IsDragStartIn"/> - shared with the
        /// retained <see cref="Slider(KhaozEngine.Primitives.Rect, float)"/>), the value tracks
        /// the pointer X clamped to [0,1] - the drag keeps control even if the cursor strays off the track, which
        /// <c>IsPressingIn</c> would not. The handle half-width is inset so the ends reach exactly 0 and 1. The
        /// caller owns the value's range mapping (volumes are already 0..1), persistence, and any side-effects.
        /// When enabled the rect is reserved for the <see cref="PointerCaptured"/> gate; when disabled the value is
        /// returned unchanged, nothing is reserved, and the control draws muted.
        /// </summary>
        public float Slider(Rect rect, float value, GuiStyle style, bool enabled = true)
        {
            Pointer p = _pointer;

            float result = value;
            bool dragging = enabled && p.IsDragStartIn(rect);
            if (dragging)
            {
                (float half, float usable) = GuiDraw.SliderGeometry(rect);
                float v = (p.Position.X - (rect.X + half)) / usable;
                result = v < 0f ? 0f : v > 1f ? 1f : v;
            }

            // Only an enabled slider blocks the layer beneath (per spec a disabled slider lets the board through).
            if (enabled) _blocked.Add(rect);

            bool hovering = enabled && p.IsHoveringIn(rect);
            if (hovering) _hoveredRect = rect;

            if (_batch is null) return result;

            GuiDraw.DrawSlider(_batch, _white, rect, result, style, enabled, hovering, dragging);
            return result;
        }

        /// <summary>
        /// True when the pointer is over an enabled <see cref="Button(SpriteFont, Rect, string)"/> this frame.
        /// Valid after all widgets for the frame have been issued (read it before the next <see cref="Begin"/>).
        /// </summary>
        public bool IsHovering => _hoveredRect.HasValue;

        /// <summary>The rect of the enabled button under the pointer this frame, or null when none is hovered.</summary>
        public Rect? HoveredRect => _hoveredRect;

        /// <summary>
        /// True only on the frame the pointer moves ONTO a (different) enabled button - i.e. a hover-enter, or
        /// sliding from one button straight onto another. False while staying on the same button, and false on
        /// hover-exit (moving off a button onto nothing). Wire this to a UI hover sound / highlight. Valid after
        /// all widgets for the frame have been issued.
        /// </summary>
        public bool HoverEntered => _hoveredRect.HasValue && _hoveredRect != _prevHoveredRect;

        /// <summary>
        /// True when the stored pointer's press-origin lies inside any widget reserved this frame (the
        /// click-through gate). Use to suppress world/board input when the user pressed on the UI. Always false
        /// while the window is unfocused (a background window captures nothing).
        /// </summary>
        public bool PointerCaptured
        {
            get
            {
                // A background window captures no input, so the click-through gate stays open while unfocused.
                if (!_pointer.WindowFocused) return false;
                // PressOrigin defaults to (0,0) and is only meaningful once a press has happened, so a
                // never-pressed pointer must not capture a widget that merely sits at the origin.
                if (!_pointer.IsDown && !_pointer.IsJustReleased) return false;
                Vector2 origin = _pointer.PressOrigin;
                foreach (var r in _blocked)
                    if (r.Contains(origin)) return true;
                return false;
            }
        }

        /// <summary>True when the pointer's CURRENT position is inside any widget rect reserved this frame
        /// (the same per-frame <c>_blocked</c> set <see cref="PointerCaptured"/> tests, but against the live
        /// position rather than the press origin, and with no press-in-progress guard). Use to suppress world
        /// HOVER affordances (tooltips, hover highlights) while the cursor is over UI. Because <c>_blocked</c>
        /// includes <see cref="Panel(Rect, Vector4)"/> rects, this covers panel backgrounds, not just the
        /// interactive widgets tracked by <see cref="HoveredRect"/>. Always false while the window is unfocused
        /// (the pointer is treated as not over anything in the background).</summary>
        public bool HoverCaptured
        {
            get
            {
                if (!_pointer.WindowFocused) return false;
                Vector2 pos = _pointer.Position;
                foreach (var r in _blocked)
                    if (r.Contains(pos)) return true;
                return false;
            }
        }
    }
}
