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
        /// <summary>The icon id resolved through <see cref="SlotGrid.IconAtlas"/>. Null draws no icon, deliberately,
        /// and never falls back. An id the atlas cannot resolve falls back to <see cref="SlotGrid.FallbackIconId"/>
        /// when set, else it also draws no icon.</summary>
        public string? IconId { get; }
        /// <summary>The icon tint, multiplied over the icon before the disabled dim is applied.</summary>
        public Vector4 Tint { get; }
        /// <summary>Remaining-cooldown fraction in [0,1]: 0 = no sweep, 1 = fully covered. Clamped on construction.</summary>
        public float Cooldown { get; }
        /// <summary>Stack / charge count drawn bottom-right (0 or less draws no number, unless
        /// <see cref="SlotGrid.CountFormatter"/> overrides the rendered text). The count only renders when a font
        /// is passed to <see cref="SlotGrid.Draw"/>.</summary>
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
    /// A grid of uniform slots (a hotbar, an inventory panel, an equipment rack) over <see cref="Pointer"/>.
    /// <see cref="Bounds"/>.X/Y is the grid's top-left origin. The footprint is DERIVED from <see cref="Columns"/>,
    /// <see cref="SlotWidth"/> / <see cref="SlotHeight"/>, <see cref="Spacing"/> and the slot <see cref="Count"/>
    /// (<see cref="Bounds"/>.Width /
    /// Height are advisory - read <see cref="ContentSize"/> / <see cref="ContentBounds"/> for the real footprint).
    /// A slot is square by default and <see cref="SlotSize"/> keeps writing both axes at once, so a panel that draws
    /// item NAMES rather than icons sets the two axes apart for a wide, short cell.
    /// Slots fill left-to-right then top-to-bottom, wrapping at <see cref="Columns"/>. Each slot is hit-tested through
    /// the press-origin <see cref="Pointer.IsTapIn"/> invariant, so a click that began in another slot (or off-grid)
    /// can't fire it, and the right button gets the same treatment through <see cref="OnSlotRightClicked"/> (the
    /// per-slot context menu). <see cref="HoveredSlot"/> / <see cref="PressedSlot"/> expose the live states (-1 = none). The
    /// widget knows nothing about game items: it draws each empty slot as a themed frame and lets the caller paint
    /// icons / counts through <see cref="DrawSlotContent"/>. Call <see cref="Update(Pointer)"/> then <see cref="Draw"/> each
    /// frame. <see cref="Update(Pointer)"/> reserves the footprint on the pointer (the click-through gate).
    /// </summary>
    public sealed class SlotGrid
    {
        /// <summary>The grid origin: only X/Y drive layout (the footprint is <see cref="ContentSize"/>).</summary>
        public Rect Bounds;

        /// <summary>Total number of slots (N). Filled left-to-right, top-to-bottom, wrapping at <see cref="Columns"/>.</summary>
        public int Count;
        /// <summary>Slots per row (the wrap width). <see cref="Rows"/> is derived from this and <see cref="Count"/>.</summary>
        public int Columns;
        /// <summary>Width of every slot, in draw units. Defaults to 48, the square cell the grid always drew.</summary>
        public float SlotWidth = 48f;
        /// <summary>Height of every slot, in draw units. Defaults to 48, the square cell the grid always drew.
        /// Set it apart from <see cref="SlotWidth"/> for a rectangular cell, e.g. an inventory row that draws an
        /// item NAME rather than an icon and so wants a wide, short slot.</summary>
        public float SlotHeight = 48f;

        /// <summary>
        /// The square shorthand: setting it writes BOTH <see cref="SlotWidth"/> and <see cref="SlotHeight"/>, which
        /// is what this knob always did back when a slot could only be square. Reading it returns
        /// <see cref="SlotWidth"/>, so on a rectangular grid read the two axes directly rather than this.
        /// </summary>
        public float SlotSize
        {
            get => SlotWidth;
            set { SlotWidth = value; SlotHeight = value; }
        }
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

        /// <summary>Icon id drawn instead when a slot's <see cref="SlotContent.IconId"/> is set but
        /// <see cref="IconAtlas"/> cannot resolve it, for example an item roster that arrives over the wire after
        /// the build shipped and names an id the atlas has never seen. A null <see cref="SlotContent.IconId"/>
        /// still means "no icon" and never falls back to this. Null (the default), or an id that itself misses
        /// <see cref="IconAtlas"/>, leaves the slot drawing no icon, same as before this field existed.</summary>
        public string? FallbackIconId;

        /// <summary>Tint of the radial cooldown sweep drawn over a slot's icon (translucent black by default).</summary>
        public Vector4 CooldownTint = GuiSurface.DefaultCooldownTint;
        /// <summary>Colour of the stack-count number drawn bottom-right in a slot.</summary>
        public Vector4 CountColor = GuiTheme.Default.Text;
        /// <summary>Uniform scale for the stack-count number (default 1).</summary>
        public float CountScale = 1f;
        /// <summary>Inset of the stack-count number from the slot's bottom-right corner, in draw units.</summary>
        public float CountPad = 3f;

        /// <summary>
        /// Formats the stack / charge count text drawn bottom-right in a slot: invoked as (slotIndex, content) and
        /// returning the text to draw, or null / empty to draw nothing. Null (the default) reproduces today's
        /// built-in behaviour exactly: a count only draws when <see cref="SlotContent.Count"/> is greater than
        /// zero, as <c>Count.ToString(CultureInfo.InvariantCulture)</c>. When set, the formatter is invoked for
        /// every slot that has content, regardless of <see cref="SlotContent.Count"/>. That is deliberate: it is
        /// what lets a game suppress the count for a single item by returning null, or render a zero-charge
        /// indicator, decisions the built-in greater-than-zero gate cannot make. Both the slot index and the full
        /// <see cref="SlotContent"/> are passed, not just the count, so one formatter can vary its output per slot
        /// (stacks in one row, charges in another) or by item kind via <see cref="SlotContent.IconId"/>. The
        /// returned text draws verbatim, in the same place with the same <see cref="CountColor"/>,
        /// <see cref="CountScale"/>, and <see cref="CountPad"/>. A count is a non-localizable numeric token, the
        /// same escape hatch <see cref="KeybindLabels"/> already documents, so this engine never resolves the
        /// returned string through localization. A game that needs a genuinely localized quantity resolves it
        /// through its own <c>LocalizationManager</c> first and hands this the already-resolved string.
        /// </summary>
        public Func<int, SlotContent, string?>? CountFormatter;

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

        /// <summary>Fired on a valid press-origin tap with the tapped slot index (mirrors the <see cref="Update(Pointer)"/> return).</summary>
        public Action<int>? OnSlotClicked;

        /// <summary>
        /// The right-button twin of <see cref="OnSlotClicked"/>, fired on a valid right press-origin tap
        /// (<see cref="Pointer.IsRightTapIn"/>) with the tapped slot index. This is what a per-slot context menu
        /// hangs off, and it carries the same press-origin guarantee the left tap does: a right press that began
        /// in another slot, or off the grid, never fires. The <c>Update</c> return value stays the LEFT tap, so a
        /// caller polling the return reads exactly what it always did. Poll <see cref="RightClickedSlot"/> instead
        /// of wiring this when the call site prefers state to a callback.
        /// </summary>
        public Action<int>? OnSlotRightClicked;

        /// <summary>Index of the slot under the pointer this frame, or -1. Set by <see cref="Update(Pointer)"/>.</summary>
        public int HoveredSlot { get; private set; } = -1;
        /// <summary>Index of the slot being pressed this frame (press began inside it), or -1. Set by <see cref="Update(Pointer)"/>.</summary>
        public int PressedSlot { get; private set; } = -1;

        /// <summary>Slot a right tap landed on this frame, or -1. The polling form of
        /// <see cref="OnSlotRightClicked"/>, and cleared at the top of every <c>Update</c> the same way
        /// <see cref="DroppedSlot"/> is.</summary>
        public int RightClickedSlot { get; private set; } = -1;

        /// <summary>
        /// Index of the slot the held press BEGAN in, or -1. Unlike <see cref="PressedSlot"/> this survives the
        /// pointer leaving that slot, because it is the press-origin query (<see cref="Pointer.IsDragStartIn"/>)
        /// rather than a per-frame containment test. That is what a drag needs: <see cref="PressedSlot"/> goes -1
        /// the instant the cursor crosses the slot edge, taking the drag's origin with it.
        /// </summary>
        public int PressOriginSlot { get; private set; } = -1;

        // The slot a drag that started in THIS grid grabbed, held for the life of that drag (see DraggingSlot).
        int _dragSourceSlot = -1;

        /// <summary>
        /// Builds the <see cref="DragPayload"/> for a drag grabbed out of a slot, invoked once as
        /// <c>(slotIndex)</c> the frame the gesture clears <see cref="GuiDragContext.DragThreshold"/>. Return null
        /// to make that slot non-draggable (an empty slot, a locked one), which blocks the arm outright rather than
        /// starting a drag the drop side would only refuse later - the same shape as
        /// <see cref="TreeView.CanReorder"/>. Null (the default) means the grid is never a drag source. The grid
        /// stays item-agnostic: what the payload CARRIES is entirely the caller's, and a payload with no
        /// <see cref="DragPayload.Ghost"/> gets the slot's own <see cref="SlotContent"/> as its ghost for free.
        /// Only consulted when an <c>Update</c> overload is given a <see cref="GuiDragContext"/>.
        /// </summary>
        public Func<int, DragPayload?>? BeginDragPayload;

        /// <summary>
        /// The drop verdict, consulted as <c>(slotIndex, payload)</c> on EVERY frame a live drag hovers a slot, not
        /// on release: returning false refuses the drop before the player lets go, so the ghost shows the refusal
        /// (<see cref="GuiDragContext.ShowRejectOverlay"/>) and nothing has to be accepted-then-undone. Null (the
        /// default) accepts any payload into any slot.
        /// </summary>
        public Func<int, DragPayload, bool>? CanAcceptDrop;

        /// <summary>Fired when a drop commits on this grid, as <c>(slotIndex, payload)</c>, before
        /// <see cref="DroppedSlot"/> is polled.</summary>
        public Action<int, DragPayload>? OnSlotDropped;

        /// <summary>Slot a live drag is hovering over THIS grid this frame, or -1 (including when a widget above
        /// claimed the pointer first). Draw the drop highlight on it.</summary>
        public int DropTargetSlot { get; private set; } = -1;
        /// <summary>Whether <see cref="DropTargetSlot"/> would take the payload: the highlight's accept / refuse colour.</summary>
        public bool DropTargetAccepted { get; private set; }

        /// <summary>Slot a drop committed on this frame, or -1. Cleared at the top of every <c>Update</c>.</summary>
        public int DroppedSlot { get; private set; } = -1;
        /// <summary>The payload dropped this frame (valid when <see cref="DroppedSlot"/> is 0 or more).</summary>
        public DragPayload DroppedPayload { get; private set; }

        /// <summary>Slot a drag that started in THIS grid is currently carrying, or -1. Live for the whole drag
        /// (unlike <see cref="PressOriginSlot"/>, which goes -1 the moment the button comes up), so the origin slot
        /// can be dimmed or blanked while its contents are in flight.</summary>
        public int DraggingSlot => _dragSourceSlot;

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
                float w = cols * SlotWidth + (cols - 1) * Spacing;
                float h = rows * SlotHeight + (rows - 1) * Spacing;
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
            float x = Bounds.X + col * (SlotWidth + Spacing);
            float y = Bounds.Y + row * (SlotHeight + Spacing);
            return new Rect(x, y, SlotWidth, SlotHeight);
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
        /// that slot index. Returns -1 otherwise. A valid right press-origin tap sets
        /// <see cref="RightClickedSlot"/> and fires <see cref="OnSlotRightClicked"/> in the same pass, and never
        /// changes the return value.
        /// </summary>
        public int Update(Pointer pointer) => Update(pointer, null);

        /// <summary>
        /// <see cref="Update(Pointer)"/> plus the drag-and-drop pass over <paramref name="drag"/>. As a SOURCE, a
        /// held press that clears <see cref="GuiDragContext.DragThreshold"/> grabs the payload
        /// <see cref="BeginDragPayload"/> returns for <see cref="PressOriginSlot"/> (null there means that slot is
        /// not draggable, and nothing arms). As a TARGET, every frame the live drag is over a slot the grid offers
        /// it with <see cref="CanAcceptDrop"/>'s verdict, so a refusal shows BEFORE the release, and a drop that
        /// commits sets <see cref="DroppedSlot"/> / <see cref="DroppedPayload"/> and fires
        /// <see cref="OnSlotDropped"/>. Passing null for <paramref name="drag"/> is exactly
        /// <see cref="Update(Pointer)"/>, and a grid with neither hook wired never takes part in a drag.
        /// </summary>
        public int Update(Pointer pointer, GuiDragContext? drag)
        {
            pointer.BlockRegion(ContentBounds);
            HoveredSlot = -1;
            PressedSlot = -1;
            PressOriginSlot = -1;
            DropTargetSlot = -1;
            DropTargetAccepted = false;
            DroppedSlot = -1;
            DroppedPayload = default;
            RightClickedSlot = -1;
            int clicked = -1;
            for (int i = 0; i < Count; i++)
            {
                Rect r = SlotRect(i);
                if (HoveredSlot < 0 && pointer.IsHoveringIn(r)) HoveredSlot = i;
                if (PressedSlot < 0 && pointer.IsPressingIn(r)) PressedSlot = i;
                if (PressOriginSlot < 0 && pointer.IsDragStartIn(r)) PressOriginSlot = i;
                if (clicked < 0 && pointer.IsTapIn(r)) clicked = i;
                if (RightClickedSlot < 0 && pointer.IsRightTapIn(r)) RightClickedSlot = i;
            }

            if (drag is not null) UpdateDrag(pointer, drag);

            if (clicked >= 0) OnSlotClicked?.Invoke(clicked);
            if (RightClickedSlot >= 0) OnSlotRightClicked?.Invoke(RightClickedSlot);
            return clicked;
        }

        // The drag pass, split out of Update so the plain hit-test loop above stays the same shape it always was.
        // Source first, then target: a grid can be both (dragging a stack from one slot onto another in the same
        // grid is the reorder case), and the two halves never collide because arming needs the button HELD while
        // committing needs it released.
        void UpdateDrag(Pointer pointer, GuiDragContext drag)
        {
            if (!drag.IsDragging) _dragSourceSlot = -1;

            if (BeginDragPayload is not null && !drag.IsDragging && PressOriginSlot >= 0)
            {
                Rect source = SlotRect(PressOriginSlot);
                if (drag.ShouldBeginDrag(pointer, source) && BeginDragPayload(PressOriginSlot) is { } payload)
                {
                    // Zero-config ghost: a payload the game built without a painter drags the slot's own built-in
                    // SlotContent (icon, cooldown sweep, count), which is already exactly what the player grabbed.
                    int from = PressOriginSlot;
                    if (payload.Ghost is null && _content.TryGetValue(from, out SlotContent grabbed))
                        payload = payload.WithGhost((b, white, font, rect) => DrawContent(b, white, font, from, rect, grabbed));

                    if (drag.Begin(pointer, payload, source)) _dragSourceSlot = from;
                }
            }

            if (!drag.IsDragging) return;

            int over = SlotAt(pointer.Position);
            if (over < 0) return;

            bool accept = CanAcceptDrop is null || CanAcceptDrop(over, drag.Payload);
            bool committed = drag.OfferTarget(this, over, accept);
            // Only report the hover state when THIS grid actually claimed the offer: an overlay above it may have
            // taken the pointer first, in which case this grid is not the target however much it overlaps.
            if (committed || ReferenceEquals(drag.HoveredTargetId, this))
            {
                DropTargetSlot = over;
                DropTargetAccepted = accept;
            }
            if (!committed) return;

            DroppedSlot = over;
            DroppedPayload = drag.LastDrop.Payload;
            OnSlotDropped?.Invoke(over, DroppedPayload);
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
                    DrawContent(batch, white, font, i, r, content);

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

        /// <summary>Test seam: the (texture, source UV) <see cref="DrawContent"/> would draw for slot content
        /// <paramref name="content"/>, resolved without a batch or GPU. Tries <see cref="SlotContent.IconId"/>
        /// first, then <see cref="FallbackIconId"/> only when <see cref="SlotContent.IconId"/> is set but misses
        /// <see cref="IconAtlas"/>. A null <see cref="SlotContent.IconId"/> deliberately means "no icon" and never
        /// falls back.</summary>
        internal bool TryResolveIcon(in SlotContent content, out Texture2D tex, out Vector4 uv)
        {
            tex = null!;
            uv = default;
            IconAtlas? atlas = IconAtlas;
            string? iconId = content.IconId;
            if (iconId == null || atlas == null) return false;
            if (atlas.TryGet(iconId, out tex, out uv)) return true;
            return FallbackIconId != null && atlas.TryGet(FallbackIconId, out tex, out uv);
        }

        /// <summary>Test seam: the count text <see cref="DrawContent"/> would draw for slot
        /// <paramref name="index"/>'s <paramref name="content"/>, normalized to null when nothing should draw (a
        /// formatter's empty-string return included). Font and batch independent, mirroring
        /// <see cref="GuiSurface.FormatStatChipText"/>. A null <see cref="CountFormatter"/> reproduces the built-in
        /// greater-than-zero gate exactly. A non-null <see cref="CountFormatter"/> is invoked unconditionally,
        /// count included, which is what <see cref="CountFormatter"/>'s own doc relies on.</summary>
        internal string? ResolveCountText(int index, in SlotContent content)
        {
            string? txt = CountFormatter != null
                ? CountFormatter(index, content)
                : content.Count > 0 ? content.Count.ToString(CultureInfo.InvariantCulture) : null;
            return string.IsNullOrEmpty(txt) ? null : txt;
        }

        // Built-in slot content, drawn between the frame and the DrawSlotContent hook: the icon (greyed when
        // disabled, resolved through TryResolveIcon), then the radial cooldown sweep over the icon rect, then the
        // stack count bottom-right (resolved through ResolveCountText). The DrawSlotContent hook still draws after
        // this, so caller-painted content composes on top.
        void DrawContent(SpriteBatch batch, Texture2D white, SpriteFont? font, int index, Rect slot, in SlotContent content)
        {
            var iconRect = new Rect(slot.X + IconInset, slot.Y + IconInset,
                slot.Width - IconInset * 2f, slot.Height - IconInset * 2f);

            if (TryResolveIcon(content, out Texture2D tex, out Vector4 uv))
            {
                Vector4 tint = content.Disabled ? DimRgb(content.Tint, DisabledIconDim) : content.Tint;
                batch.Draw(tex, new Vector4(iconRect.X, iconRect.Y, iconRect.Width, iconRect.Height), uv,
                    (Color)GuiDraw.WithOpacity(tint, Opacity));
            }

            if (content.Cooldown > 0f)
                GuiDraw.CooldownSweep(batch, white, iconRect, content.Cooldown, GuiDraw.WithOpacity(CooldownTint, Opacity));

            // The stack count text comes from ResolveCountText, already normalized to null for "draw nothing": the
            // built-in greater-than-zero gate when CountFormatter is null, or the game's own formatter otherwise.
            // Either way it is a non-localizable escape hatch, the same rationale as the keybind glyphs. See
            // CountFormatter's XML doc for the full contract. It needs the font Draw already receives.
            if (font != null)
            {
                string? txt = ResolveCountText(index, content);
                if (txt != null)
                {
                    Vector2 m = font.Measure(txt) * CountScale;
                    var pos = new Vector2(slot.Right - m.X - CountPad, slot.Bottom - font.LineHeight * CountScale - CountPad);
                    batch.DrawString(font, txt, pos, (Color)GuiDraw.WithOpacity(CountColor, Opacity), CountScale);
                }
            }
        }

        // Multiply RGB by factor, keep alpha (a greyed icon that stays opaque under straight-alpha blending).
        static Vector4 DimRgb(Vector4 c, float factor) => new(c.X * factor, c.Y * factor, c.Z * factor, c.W);
    }
}
