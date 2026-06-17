using System.Collections.Generic;
using System.Numerics;
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
    /// Use the retained widgets (<see cref="Button"/>, <see cref="Toggle"/>, <see cref="Slider"/>,
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

        /// <summary>The style applied to <see cref="Button(SpriteFont, Rect, string)"/> when no explicit style is passed.</summary>
        public GuiStyle Style { get; set; }

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

        /// <summary>Draw a plain filled colour chip; reserves it for click-through.</summary>
        public void Swatch(Rect rect, Vector4 color)
        {
            _blocked.Add(rect);
            if (_batch is null) return;
            GuiDraw.Fill(_batch, _white, rect, color);
        }

        /// <summary>Draw <paramref name="text"/> at <paramref name="pos"/> (top-left). Does not reserve any rect.</summary>
        public void Label(SpriteFont font, string text, Vector2 pos, Vector4 color)
        {
            if (_batch is null) return;
            _batch.DrawString(font, text, pos, color);
        }

        /// <summary>
        /// Draw <paramref name="text"/> aligned within <paramref name="rect"/> horizontally per
        /// <paramref name="align"/> and vertically centered. Does not reserve any rect.
        /// </summary>
        public void Label(SpriteFont font, Rect rect, string text, Vector4 color, GuiAlign align = GuiAlign.Center)
        {
            if (_batch is null) return;
            const float pad = 6f;
            Vector2 size = font.Measure(text);
            float x = align switch
            {
                GuiAlign.Left => rect.X + pad,
                GuiAlign.Right => rect.Right - size.X - pad,
                _ => rect.X + (rect.Width - size.X) * 0.5f,
            };
            float y = rect.Y + (rect.Height - font.LineHeight) * 0.5f;
            _batch.DrawString(font, text, new Vector2(x, y), color);
        }

        /// <summary>A button with the surface's default <see cref="Style"/>. Returns true on a valid press-origin tap.</summary>
        public bool Button(SpriteFont font, Rect rect, string label) =>
            Button(font, rect, label, Style);

        /// <summary>
        /// A button with hover/press/disabled/selected visuals. Returns true on the release frame of a tap whose
        /// press began inside <paramref name="rect"/> (the <see cref="Pointer.IsTapIn"/> invariant), only when
        /// <paramref name="enabled"/>. Always reserves its rect for click-through (even disabled).
        /// </summary>
        public bool Button(SpriteFont font, Rect rect, string label, GuiStyle style, bool enabled = true, bool selected = false)
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
            GuiDraw.DrawButton(_batch, _white, font, rect, label, style, enabled, selected, hovering, pressing);

            return clicked;
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
        /// click-through gate). Use to suppress world/board input when the user pressed on the UI.
        /// </summary>
        public bool PointerCaptured
        {
            get
            {
                // PressOrigin defaults to (0,0) and is only meaningful once a press has happened, so a
                // never-pressed pointer must not capture a widget that merely sits at the origin.
                if (!_pointer.IsDown && !_pointer.IsJustReleased) return false;
                Vector2 origin = _pointer.PressOrigin;
                foreach (var r in _blocked)
                    if (r.Contains(origin)) return true;
                return false;
            }
        }
    }
}
