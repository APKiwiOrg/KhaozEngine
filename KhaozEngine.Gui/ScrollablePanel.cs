using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A vertically-scrolling list region over <see cref="Pointer"/>: the wheel (while hovering) and dragging
    /// inside scroll a fixed-height item list, clamped to range. The owner draws each item itself, positioned via
    /// <see cref="ItemBounds"/>, between <see cref="BeginClip"/> and <see cref="EndClip"/> (which set/clear the
    /// SpriteBatch scissor so rows are clipped to the content region). Hit-test rows with
    /// <see cref="TappedItemIndex"/>. Clipping is via the engine's <see cref="SpriteBatch"/> scissor.
    /// <para>
    /// Opt-in overlay chrome (9.21.0), all defaulting to no-ops so existing callers are byte-identical: a header
    /// band (<see cref="HeaderHeight"/> + <see cref="DrawHeader"/>) above the scroll region; a slide-up animation
    /// driven by an external <see cref="TransitionAlpha"/> from a docked bottom edge (<see cref="SlideFromBottom"/>);
    /// drag-to-resize the header within <see cref="MinHeight"/>/<see cref="MaxHeight"/> (<see cref="Resizable"/>);
    /// and a dimmed <see cref="Scrim"/> with tap-outside-to-close (<see cref="ScrimDismissed"/>). All geometry is
    /// computed off <see cref="CurrentBounds"/> (== <see cref="Bounds"/> when no overlay knob is set).
    /// </para>
    /// </summary>
    public sealed class ScrollablePanel
    {
        /// <summary>The docked, fully-open panel rect the caller owns. Overlay knobs slide/resize a copy of it
        /// (<see cref="CurrentBounds"/>); with no knobs set <see cref="CurrentBounds"/> equals this exactly.</summary>
        public Rect Bounds;
        public int ItemCount;
        public float ItemHeight = 40f;
        public float ItemSpacing = 4f;
        /// <summary>Pixels scrolled per wheel notch.</summary>
        public float WheelSpeed = 30f;
        /// <summary>When true, <see cref="Update"/> reserves <see cref="CurrentBounds"/> on the pointer (so layers beneath skip it).</summary>
        public bool BlocksPointer = true;

        public Vector4 Background = new(0.047f, 0.047f, 0.078f, 0.96f);
        public Vector4 Border = new(0.24f, 0.24f, 0.31f, 1f);

        /// <summary>
        /// Modern-look knobs (rounded/shadow/gradient/glow) for the panel background; defaults to the flat
        /// <see cref="GuiStyle.Default"/> so the panel renders byte-identically to pre-7.8.0. Keeps its own
        /// <see cref="Background"/>/<see cref="Border"/> colours. Note the row clip (<see cref="BeginClip"/>) is a
        /// rectangular scissor, so a large corner radius can leave items square at the corners. Set
        /// <c>Style = GuiStyle.Modern</c> to opt in.
        /// </summary>
        public GuiStyle Style = GuiStyle.Default;

        // ---- opt-in overlay chrome (9.21.0) --------------------------------------------------------------

        /// <summary>Height of a header band reserved at the top of the panel (title + divider). 0 (default) = no
        /// header: the content region fills the whole panel, exactly as before. Draw it with <see cref="DrawHeader"/>.</summary>
        public float HeaderHeight = 0f;
        public Vector4 HeaderBackground = new(0f, 0f, 0f, 0f);   // transparent by default: header sits over the panel bg
        public Vector4 HeaderDivider = new(0.20f, 0.20f, 0.25f, 1f);
        public Vector4 HeaderTextColor = new(0.90f, 0.92f, 0.96f, 1f);

        /// <summary>Slide progress 0..1: 1 = fully shown at the natural top, 0 = fully hidden below the docked
        /// bottom edge. Only affects geometry when <see cref="SlideFromBottom"/> is set. Default 1 = no offset.</summary>
        public float TransitionAlpha = 1f;
        /// <summary>Opt-in: map <see cref="TransitionAlpha"/> to a vertical slide up from the panel's bottom edge.</summary>
        public bool SlideFromBottom = false;

        /// <summary>Opt-in: let a drag on the header band resize the panel height (docked to the bottom edge),
        /// clamped to <see cref="MinHeight"/>..<see cref="MaxHeight"/>. Needs <see cref="HeaderHeight"/> &gt; 0.</summary>
        public bool Resizable = false;
        public float MinHeight = 0f;
        public float MaxHeight = float.MaxValue;

        /// <summary>Optional dimmed backdrop behind the panel. When set, <see cref="Update"/> reserves it on the
        /// pointer and reports <see cref="ScrimDismissed"/> when it is tapped outside the panel; draw it with
        /// <see cref="DrawScrim"/>. Null (default) = no scrim.</summary>
        public Rect? Scrim = null;
        public Vector4 ScrimColor = new(0f, 0f, 0f, 1f);
        /// <summary>Scrim opacity at full <see cref="TransitionAlpha"/>; the drawn alpha is <c>ScrimAlpha * TransitionAlpha</c>.</summary>
        public float ScrimAlpha = 0.4f;
        /// <summary>True on the frame a scrim gesture that both began and ended outside the panel dismissed it (the
        /// caller should close the panel). A gesture whose press originated on the panel never dismisses, so a
        /// scroll-drag on the list that releases over the scrim above the panel is not a dismiss.</summary>
        public bool ScrimDismissed { get; private set; }

        float? _resizeHeight;
        bool _resizing;

        public float ScrollOffset { get; private set; }
        public float Stride => ItemHeight + ItemSpacing;
        public float ContentHeight => ItemCount * Stride;

        /// <summary>The panel's height this frame after any drag-resize (docked to <see cref="Bounds"/>'s bottom edge).</summary>
        public float EffectiveHeight => Resizable
            ? Math.Clamp(_resizeHeight ?? Bounds.Height, MinHeight, MaxHeight)
            : Bounds.Height;

        /// <summary>The on-screen panel rect this frame: <see cref="Bounds"/> after optional resize + slide. Equals
        /// <see cref="Bounds"/> when no overlay knob is set.</summary>
        public Rect CurrentBounds
        {
            get
            {
                float slide = SlideFromBottom ? (1f - TransitionAlpha) * EffectiveHeight : 0f;
                float top = Bounds.Bottom - EffectiveHeight + slide;
                return new Rect(Bounds.X, top, Bounds.Width, EffectiveHeight);
            }
        }

        /// <summary>The header band at the top of <see cref="CurrentBounds"/> (empty when <see cref="HeaderHeight"/> is 0).</summary>
        public Rect HeaderBounds => new(CurrentBounds.X, CurrentBounds.Y, CurrentBounds.Width, HeaderHeight);

        /// <summary>The scrollable content region below the header band.</summary>
        public Rect ContentBounds
        {
            get
            {
                Rect c = CurrentBounds;
                return new Rect(c.X, c.Y + HeaderHeight, c.Width, MathF.Max(0f, c.Height - HeaderHeight));
            }
        }

        /// <summary>Max scroll = content height minus the visible content viewport (below the header), clamped &gt;= 0.</summary>
        public float MaxScroll => MathF.Max(0f, ContentHeight - ContentBounds.Height);

        public ScrollablePanel(Rect bounds) { Bounds = bounds; }

        /// <summary>Jump to a scroll offset (clamped to range).</summary>
        public void ScrollTo(float offset) => ScrollOffset = Math.Clamp(offset, 0f, MaxScroll);

        /// <summary>Apply wheel + drag scrolling for this frame, drive header-resize + scrim, and (optionally) reserve the pointer.</summary>
        public void Update(Pointer pointer, InputState input)
        {
            ScrimDismissed = false;

            if (Scrim.HasValue)
            {
                pointer.BlockRegion(Scrim.Value);
                // A consumer's scrim can legitimately span the whole surface, including behind the panel, so
                // IsTapIn(Scrim) alone is satisfied by a gesture that began ON the panel (a scroll-drag on the
                // list that strays up past the panel edge before release). Guard on the press origin too: a
                // gesture that started on the panel must never dismiss, only one that both began and ended
                // outside it.
                if (pointer.IsTapIn(Scrim.Value)
                    && !CurrentBounds.Contains(pointer.PressOrigin)
                    && !CurrentBounds.Contains(pointer.Position))
                    ScrimDismissed = true;
            }

            if (BlocksPointer) pointer.BlockRegion(CurrentBounds);

            // Header drag-resize (docked to the bottom edge). Latched so it keeps tracking once the header slides
            // away under the cursor; a press that began in the header owns the drag (press-origin invariant).
            if (Resizable && HeaderHeight > 0f)
            {
                if (_resizing)
                {
                    if (pointer.IsDown)
                        _resizeHeight = Math.Clamp((_resizeHeight ?? Bounds.Height) - pointer.Delta.Y, MinHeight, MaxHeight);
                    else
                        _resizing = false;
                }
                else if (pointer.IsDragStartIn(HeaderBounds))
                {
                    _resizing = true;
                }
            }

            Rect content = ContentBounds;

            if (pointer.IsPointerIn(content) && input.ScrollDelta != 0f)
                ScrollOffset -= input.ScrollDelta * WheelSpeed;

            // Drag-pan only when the drag began in the content region AND we are not mid-resize. The `_resizing`
            // guard is load-bearing: a header drag that grows the panel moves ContentBounds up past the fixed
            // press-origin, so GetDragDelta's press-origin test would start returning the drag delta and pan the
            // list while the user is only resizing. Latching on _resizing keeps resize and scroll mutually exclusive.
            float dragY = _resizing ? 0f : pointer.GetDragDelta(content).Y;
            if (dragY != 0f)
                ScrollOffset -= dragY;

            ScrollOffset = Math.Clamp(ScrollOffset, 0f, MaxScroll);
        }

        /// <summary>The on-screen bounds of item <paramref name="index"/> (accounting for scroll). May lie outside <see cref="ContentBounds"/>.</summary>
        public Rect ItemBounds(int index)
        {
            Rect c = ContentBounds;
            return new(c.X, c.Y + index * Stride - ScrollOffset, c.Width, ItemHeight);
        }

        /// <summary>The item index under a tap (release inside the content region and on a row, not a gap), or -1.</summary>
        public int TappedItemIndex(Pointer pointer)
        {
            Rect c = ContentBounds;
            if (!pointer.IsTapIn(c)) return -1;
            float rel = pointer.Position.Y - c.Y + ScrollOffset;
            int idx = (int)(rel / Stride);
            if (idx < 0 || idx >= ItemCount) return -1;
            float top = idx * Stride;
            if (rel < top || rel > top + ItemHeight) return -1;   // in the spacing gap
            return idx;
        }

        /// <summary>Draw the dimmed scrim (if <see cref="Scrim"/> is set); fades with <see cref="TransitionAlpha"/>. Call before <see cref="DrawBackground"/>.</summary>
        public void DrawScrim(SpriteBatch batch, Texture2D white)
        {
            if (!Scrim.HasValue) return;
            GuiDraw.Fill(batch, white, Scrim.Value, GuiDraw.WithOpacity(ScrimColor, ScrimAlpha * TransitionAlpha));
        }

        /// <summary>Draw the panel background + border at <see cref="CurrentBounds"/>. <paramref name="white"/> is a 1x1 white texture.</summary>
        public void DrawBackground(SpriteBatch batch, Texture2D white)
        {
            GuiDraw.FillStyled(batch, white, CurrentBounds, Style with { BorderThickness = 1f }, Background, Border);
        }

        /// <summary>Draw the header band: optional <see cref="HeaderBackground"/> fill, left-aligned <paramref name="title"/>,
        /// and a bottom divider. No-op when <see cref="HeaderHeight"/> is 0.</summary>
        public void DrawHeader(SpriteBatch batch, Texture2D white, SpriteFont font, string title)
        {
            if (HeaderHeight <= 0f) return;
            Rect h = HeaderBounds;
            if (HeaderBackground.W > 0f) GuiDraw.Fill(batch, white, h, HeaderBackground);
            if (!string.IsNullOrEmpty(title))
            {
                float ty = h.Y + (HeaderHeight - font.LineHeight) * 0.5f;
                batch.DrawString(font, title, new Vector2(MathF.Floor(h.X + 8f), MathF.Floor(ty)), (Color)HeaderTextColor);
            }
            GuiDraw.Fill(batch, white, new Rect(h.X, h.Bottom - 1f, h.Width, 1f), HeaderDivider);
        }

        /// <summary>Flush pending draws and clip subsequent draws to <see cref="ContentBounds"/>. Draw items, then call <see cref="EndClip"/>.</summary>
        public void BeginClip(SpriteBatch batch) => batch.SetScissor(ContentBounds);

        /// <summary>Flush the clipped draws and restore the full viewport.</summary>
        public void EndClip(SpriteBatch batch) => batch.ClearScissor();
    }
}
