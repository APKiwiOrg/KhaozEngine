using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// The drawable content of one <see cref="SlotGrid"/> slot: a built-in icon (resolved through the grid's
    /// <see cref="SlotGrid.IconAtlas"/>, the same registry <see cref="GuiSurface.Icon"/> uses), an optional radial
    /// cooldown sweep, an optional stack / charge count, and a disabled (greyed) flag. A readonly value: build one
    /// and hand it to <see cref="SlotGrid.SetContent"/>.
    /// </summary>
    public readonly struct SlotContent
    {
        /// <summary>The icon id resolved through <see cref="SlotGrid.IconAtlas"/> (null or an unknown id draws no icon).</summary>
        public string? IconId { get; }
        /// <summary>The icon tint, multiplied over the icon before the disabled dim is applied.</summary>
        public Vector4 Tint { get; }
        /// <summary>Remaining-cooldown fraction in [0,1]: 0 = no sweep, 1 = fully covered. Clamped on construction.</summary>
        public float Cooldown { get; }
        /// <summary>Stack / charge count drawn bottom-right (0 or less draws no number). The count only renders when a font is passed to <see cref="SlotGrid.Draw"/>.</summary>
        public int Count { get; }
        /// <summary>When true the icon draws greyed (RGB dimmed) so the slot reads as unavailable.</summary>
        public bool Disabled { get; }

        /// <summary>Full content: an icon id, its <paramref name="tint"/>, and optional cooldown / count / disabled state.</summary>
        public SlotContent(string? iconId, Vector4 tint, float cooldown = 0f, int count = 0, bool disabled = false)
        {
            IconId = iconId;
            Tint = tint;
            Cooldown = cooldown < 0f ? 0f : cooldown > 1f ? 1f : cooldown;
            Count = count;
            Disabled = disabled;
        }

        /// <summary>Content with a white icon tint and no cooldown / count / disabled state.</summary>
        public SlotContent(string? iconId) : this(iconId, Vector4.One) { }
    }

    /// <summary>
    /// A grid of uniform square slots (a hotbar, an inventory panel, an equipment rack) over <see cref="Pointer"/>.
    /// <see cref="Bounds"/>.X/Y is the grid's top-left origin. The footprint is DERIVED from <see cref="Columns"/>,
    /// <see cref="SlotSize"/>, <see cref="Spacing"/> and the slot <see cref="Count"/> (<see cref="Bounds"/>.Width /
    /// Height are advisory - read <see cref="ContentSize"/> / <see cref="ContentBounds"/> for the real footprint).
    /// Slots fill left-to-right then top-to-bottom, wrapping at <see cref="Columns"/>. Each slot is hit-tested through
    /// the press-origin <see cref="Pointer.IsTapIn"/> invariant, so a click that began in another slot (or off-grid)
    /// can't fire it. <see cref="HoveredSlot"/> / <see cref="PressedSlot"/> expose the live states (-1 = none). The
    /// widget knows nothing about game items: it draws each empty slot as a themed frame and lets the caller paint
    /// icons / counts through <see cref="DrawSlotContent"/>. Call <see cref="Update"/> then <see cref="Draw"/> each
    /// frame. <see cref="Update"/> reserves the footprint on the pointer (the click-through gate).
    /// </summary>
    public sealed class SlotGrid
    {
        /// <summary>The grid origin: only X/Y drive layout (the footprint is <see cref="ContentSize"/>).</summary>
        public Rect Bounds;

        /// <summary>Total number of slots (N). Filled left-to-right, top-to-bottom, wrapping at <see cref="Columns"/>.</summary>
        public int Count;
        /// <summary>Slots per row (the wrap width). <see cref="Rows"/> is derived from this and <see cref="Count"/>.</summary>
        public int Columns;
        /// <summary>Edge length of every (square) slot, in draw units.</summary>
        public float SlotSize = 48f;
        /// <summary>Gap between adjacent slots (both axes), in draw units.</summary>
        public float Spacing = 4f;

        /// <summary>Resting slot fill.</summary>
        public Vector4 SlotColor = GuiTheme.Default.Surface;
        /// <summary>Slot fill under the pointer.</summary>
        public Vector4 HoverColor = GuiTheme.Default.SurfaceHover;
        /// <summary>Slot fill while pressed.</summary>
        public Vector4 PressColor = GuiTheme.Default.SurfacePress;
        /// <summary>Resting slot border.</summary>
        public Vector4 BorderColor = GuiTheme.Default.Border;
        /// <summary>Slot border under the pointer.</summary>
        public Vector4 BorderHoverColor = GuiTheme.Default.BorderHover;

        /// <summary>Look knobs (corners / shadow / glow) applied to every slot frame, defaulting to
        /// <see cref="GuiStyle.Default"/>.</summary>
        public GuiStyle Style = GuiStyle.Default;
        /// <summary>Uniform fade multiplied into every colour's alpha at draw time (1 = opaque). Mirrors
        /// <see cref="Slider.Opacity"/> so a HUD can fade the whole grid in / out.</summary>
        public float Opacity = 1f;

        /// <summary>Optional per-slot keybind glyphs drawn small in each slot's top-left corner (a null array, a short
        /// array, or a null / empty element all mean "no label" for that slot). These are non-localizable input tokens
        /// (the same escape hatch as a number), so they are raw strings the caller supplies from its binding system.</summary>
        public string?[]? KeybindLabels;
        /// <summary>Keybind-label colour.</summary>
        public Vector4 KeybindLabelColor = GuiTheme.Default.TextMuted;
        /// <summary>Uniform scale for the keybind label (default 1). Set below 1 for a smaller glyph.</summary>
        public float KeybindLabelScale = 1f;
        /// <summary>Inset of the keybind label from the slot's top-left corner, in draw units.</summary>
        public float KeybindLabelPad = 3f;

        /// <summary>The icon registry used to resolve <see cref="SlotContent.IconId"/> to a texture + source UV (the
        /// same instance mechanism <see cref="GuiSurface.Icon"/> uses). Null = built-in slot icons draw nothing.</summary>
        public IconAtlas? IconAtlas { get; set; }

        /// <summary>Tint of the radial cooldown sweep drawn over a slot's icon (translucent black by default).</summary>
        public Vector4 CooldownTint = GuiSurface.DefaultCooldownTint;
        /// <summary>Colour of the stack-count number drawn bottom-right in a slot.</summary>
        public Vector4 CountColor = GuiTheme.Default.Text;
        /// <summary>Uniform scale for the stack-count number (default 1).</summary>
        public float CountScale = 1f;
        /// <summary>Inset of the stack-count number from the slot's bottom-right corner, in draw units.</summary>
        public float CountPad = 3f;
        /// <summary>Inset of a slot's built-in icon (and its cooldown sweep) from the slot edges, in draw units.</summary>
        public float IconInset = 4f;

        // A disabled icon multiplies its RGB by this (alpha preserved) so it reads as greyed-out without going
        // translucent under straight-alpha blending (the same rationale as Color.ScaleRgb).
        const float DisabledIconDim = 0.4f;

        // Per-slot content, stored sparsely by slot index so it survives Count changes: an index outside [0, Count)
        // is simply not drawn (and draws again if the grid grows back). Mirrors the tolerant, caller-owned nature of
        // KeybindLabels (which the draw path bounds-guards) rather than a Count-sized array that would need resizing.
        readonly Dictionary<int, SlotContent> _content = new();

        /// <summary>Per-slot content painter, invoked as <c>(slotIndex, slotRect, batch)</c> after the frame is drawn,
        /// so the caller can render an icon / count without the widget knowing about items. Null = frame-only slots.</summary>
        public Action<int, Rect, SpriteBatch>? DrawSlotContent;

        /// <summary>Fired on a valid press-origin tap with the tapped slot index (mirrors the <see cref="Update"/> return).</summary>
        public Action<int>? OnSlotClicked;

        /// <summary>Index of the slot under the pointer this frame, or -1. Set by <see cref="Update"/>.</summary>
        public int HoveredSlot { get; private set; } = -1;
        /// <summary>Index of the slot being pressed this frame (press began inside it), or -1. Set by <see cref="Update"/>.</summary>
        public int PressedSlot { get; private set; } = -1;

        /// <summary>Create a grid of <paramref name="count"/> slots wrapping at <paramref name="columns"/> per row.</summary>
        public SlotGrid(Rect bounds, int count, int columns)
        {
            Bounds = bounds;
            Count = count;
            Columns = Math.Max(1, columns);
        }

        /// <summary>Number of rows the grid occupies: <c>ceil(Count / Columns)</c> (0 when empty).</summary>
        public int Rows => Count <= 0 ? 0 : (Count + Math.Max(1, Columns) - 1) / Math.Max(1, Columns);

        /// <summary>The grid's total footprint (width x height) in draw units. Pure geometry.</summary>
        public Vector2 ContentSize
        {
            get
            {
                if (Count <= 0) return Vector2.Zero;
                int cols = Math.Min(Count, Math.Max(1, Columns));   // a partial single row is only Count wide
                int rows = Rows;
                float w = cols * SlotSize + (cols - 1) * Spacing;
                float h = rows * SlotSize + (rows - 1) * Spacing;
                return new Vector2(w, h);
            }
        }

        /// <summary>The origin plus footprint as a rect (the click-through region and a caller layout handle).</summary>
        public Rect ContentBounds => new(Bounds.X, Bounds.Y, ContentSize.X, ContentSize.Y);

        /// <summary>The rect of slot <paramref name="index"/> (0-based), derived from the origin and layout. Pure geometry.</summary>
        public Rect SlotRect(int index)
        {
            int cols = Math.Max(1, Columns);
            int col = index % cols;
            int row = index / cols;
            float x = Bounds.X + col * (SlotSize + Spacing);
            float y = Bounds.Y + row * (SlotSize + Spacing);
            return new Rect(x, y, SlotSize, SlotSize);
        }

        /// <summary>Index of the slot containing <paramref name="point"/>, or -1 when the point is off every slot
        /// (the inter-slot gaps count as off). Pure geometry, independent of pointer state.</summary>
        public int SlotAt(Vector2 point)
        {
            for (int i = 0; i < Count; i++)
                if (SlotRect(i).Contains(point)) return i;
            return -1;
        }

        /// <summary>Set (or replace) the drawable <see cref="SlotContent"/> of slot <paramref name="index"/>. The
        /// content is drawn by <see cref="Draw"/> between the slot frame and the <see cref="DrawSlotContent"/> hook.
        /// Stored sparsely, so it survives <see cref="Count"/> changes.</summary>
        public void SetContent(int index, in SlotContent content)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            _content[index] = content;
        }

        /// <summary>Remove any <see cref="SlotContent"/> set on slot <paramref name="index"/> (no-op if none).</summary>
        public void ClearContent(int index) => _content.Remove(index);

        /// <summary>Remove all per-slot <see cref="SlotContent"/>.</summary>
        public void ClearAllContent() => _content.Clear();

        /// <summary>Test seam: the content set on slot <paramref name="index"/>, if any.</summary>
        internal bool TryGetContent(int index, out SlotContent content) => _content.TryGetValue(index, out content);

        /// <summary>Test seam: the number of slots that currently have content (independent of <see cref="Count"/>).</summary>
        internal int ContentCount => _content.Count;

        /// <summary>
        /// Reserve the grid footprint for click-through, then hit-test every slot. Sets <see cref="HoveredSlot"/> and
        /// <see cref="PressedSlot"/>, and on a valid press-origin tap fires <see cref="OnSlotClicked"/> and returns
        /// that slot index. Returns -1 otherwise.
        /// </summary>
        public int Update(Pointer pointer)
        {
            pointer.BlockRegion(ContentBounds);
            HoveredSlot = -1;
            PressedSlot = -1;
            int clicked = -1;
            for (int i = 0; i < Count; i++)
            {
                Rect r = SlotRect(i);
                if (HoveredSlot < 0 && pointer.IsHoveringIn(r)) HoveredSlot = i;
                if (PressedSlot < 0 && pointer.IsPressingIn(r)) PressedSlot = i;
                if (clicked < 0 && pointer.IsTapIn(r)) clicked = i;
            }
            if (clicked >= 0) OnSlotClicked?.Invoke(clicked);
            return clicked;
        }

        /// <summary>
        /// Draw every slot frame (with its hover / press state), then the per-slot <see cref="DrawSlotContent"/> and
        /// the keybind label on top. <paramref name="white"/> is a 1x1 white texture. <paramref name="font"/> renders
        /// <see cref="KeybindLabels"/> and is only needed when they are set.
        /// </summary>
        public void Draw(SpriteBatch batch, Texture2D white, SpriteFont? font = null)
        {
            for (int i = 0; i < Count; i++)
            {
                Rect r = SlotRect(i);
                bool hover = i == HoveredSlot;
                bool press = i == PressedSlot;
                Vector4 fill = press ? PressColor : hover ? HoverColor : SlotColor;
                Vector4 border = hover ? BorderHoverColor : BorderColor;

                if (hover) GuiDraw.HoverGlow(batch, white, r, Style);
                GuiDraw.FillStyled(batch, white, r, Style,
                    GuiDraw.WithOpacity(fill, Opacity), GuiDraw.WithOpacity(border, Opacity));

                if (_content.TryGetValue(i, out SlotContent content))
                    DrawContent(batch, white, font, r, content);

                DrawSlotContent?.Invoke(i, r, batch);

                if (font != null) DrawKeybindLabel(batch, font, i, r);
            }
        }

        void DrawKeybindLabel(SpriteBatch batch, SpriteFont font, int index, Rect slot)
        {
            if (KeybindLabels is null || index >= KeybindLabels.Length) return;
            string? label = KeybindLabels[index];
            if (string.IsNullOrEmpty(label)) return;
            var pos = new Vector2(slot.X + KeybindLabelPad, slot.Y + KeybindLabelPad);
            batch.DrawString(font, label, pos, (Color)GuiDraw.WithOpacity(KeybindLabelColor, Opacity), KeybindLabelScale);
        }

        // Built-in slot content, drawn between the frame and the DrawSlotContent hook: the icon (greyed when
        // disabled), then the radial cooldown sweep over the icon rect, then the stack count bottom-right. The
        // DrawSlotContent hook still draws after this, so caller-painted content composes on top.
        void DrawContent(SpriteBatch batch, Texture2D white, SpriteFont? font, Rect slot, in SlotContent content)
        {
            var iconRect = new Rect(slot.X + IconInset, slot.Y + IconInset,
                slot.Width - IconInset * 2f, slot.Height - IconInset * 2f);

            if (content.IconId != null && IconAtlas != null &&
                IconAtlas.TryGet(content.IconId, out Texture2D tex, out Vector4 uv))
            {
                Vector4 tint = content.Disabled ? DimRgb(content.Tint, DisabledIconDim) : content.Tint;
                batch.Draw(tex, new Vector4(iconRect.X, iconRect.Y, iconRect.Width, iconRect.Height), uv,
                    (Color)GuiDraw.WithOpacity(tint, Opacity));
            }

            if (content.Cooldown > 0f)
                GuiDraw.CooldownSweep(batch, white, iconRect, content.Cooldown, GuiDraw.WithOpacity(CooldownTint, Opacity));

            // The stack count is a non-localizable number (the same escape hatch as the keybind glyphs), so it is a
            // raw ToString. It needs the font Draw already receives.
            if (font != null && content.Count > 0)
            {
                string txt = content.Count.ToString(CultureInfo.InvariantCulture);
                Vector2 m = font.Measure(txt) * CountScale;
                var pos = new Vector2(slot.Right - m.X - CountPad, slot.Bottom - font.LineHeight * CountScale - CountPad);
                batch.DrawString(font, txt, pos, (Color)GuiDraw.WithOpacity(CountColor, Opacity), CountScale);
            }
        }

        // Multiply RGB by factor, keep alpha (a greyed icon that stays opaque under straight-alpha blending).
        static Vector4 DimRgb(Vector4 c, float factor) => new(c.X * factor, c.Y * factor, c.Z * factor, c.W);
    }
}
