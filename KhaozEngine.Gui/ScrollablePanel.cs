using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A vertically-scrolling list region over <see cref="Pointer"/>: the wheel (while hovering) and optional
    /// dragging inside scroll a fixed-height item list, clamped to range. The owner draws each item itself, positioned via
    /// <see cref="ItemBounds"/>, between <see cref="BeginClip"/> and <see cref="EndClip"/> (which set/clear the
    /// SpriteBatch scissor so rows are clipped to the content region). Hit-test rows with
    /// <see cref="TappedItemIndex"/>. Clipping is via the engine's <see cref="SpriteBatch"/> scissor.
    /// <para>
    /// Opt-in overlay chrome (9.21.0), all defaulting to no-ops so existing callers are byte-identical: a header
    /// band (<see cref="HeaderHeight"/> + <see cref="DrawHeader(SpriteBatch, Texture2D, SpriteFont, LocalizedText)"/>) above the scroll region; a slide-up animation
    /// driven by an external <see cref="TransitionAlpha"/> from a docked bottom edge (<see cref="SlideFromBottom"/>);
    /// drag-to-resize the header within <see cref="MinHeight"/>/<see cref="MaxHeight"/> (<see cref="Resizable"/>);
    /// and a dimmed <see cref="Scrim"/> with tap-outside-to-close (<see cref="ScrimDismissed"/>). All geometry is
    /// computed off <see cref="CurrentBounds"/> (== <see cref="Bounds"/> when no overlay knob is set).
    /// </para>
    /// <para>
    /// Opt-in height glide (10.121.0): <see cref="HeightGlideSeconds"/> smooths a content-driven height change
    /// (e.g. <see cref="ItemCount"/> changing while the panel is open) instead of snapping every frame, so
    /// <see cref="EffectiveHeight"/> (and everything derived from it) eases toward its target. 0 (default) is a
    /// no-op, byte-identical to before this knob existed. Fed by the <see cref="Update(Pointer,InputState,float)"/>
    /// dt overload; the legacy <see cref="Update(Pointer,InputState)"/> overload never glides.
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
        /// <summary>Whether a pointer drag that begins inside the content region pans the list. True by default.
        /// Disable it when the content owns dragging for another interaction. Wheel scrolling is unaffected.</summary>
        public bool DragScrollingEnabled = true;
        /// <summary>When true, <see cref="Update(Pointer,InputState)"/> reserves <see cref="CurrentBounds"/> on the pointer (so layers beneath skip it).</summary>
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
        /// header: the content region fills the whole panel, exactly as before. Draw it with <see cref="DrawHeader(SpriteBatch, Texture2D, SpriteFont, LocalizedText)"/>.</summary>
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

        /// <summary>Opt-in smooth height glide (10.121.0): the exponential time constant, in seconds, that
        /// <see cref="EffectiveHeight"/> takes to approach a changed target height while the panel stays visible
        /// (e.g. <see cref="ItemCount"/> changing async, or a tab switch). 0 (default) turns the feature off:
        /// <see cref="EffectiveHeight"/> snaps to the target every frame, exactly as before this knob existed.
        /// When &gt; 0, each dt-fed <see cref="Update(Pointer,InputState,float)"/> call eases the rendered height
        /// with <c>current += (target - current) * (1 - exp(-dt / HeightGlideSeconds))</c>, snapping once within
        /// 0.5px of the target. The glide always snaps (no easing) on the first update after construction and
        /// whenever the panel is fully hidden (<see cref="TransitionAlpha"/> &lt;= 0), so a panel always OPENS
        /// directly at its needed height: only a target change while already visible glides. While the user is
        /// actively drag-resizing (<see cref="Resizable"/>), the dragged height applies directly with no glide
        /// fighting the pointer; releasing the drag resumes the glide from wherever the drag left it. Only the dt
        /// overload advances the glide: a caller that never feeds dt (the legacy <see cref="Update(Pointer,InputState)"/>
        /// overload) gets no glide regardless of this value, exactly as before. Caveat: the open-at-target
        /// guarantee relies on the hidden-snap rule running, so the panel must keep receiving dt-fed updates
        /// while hidden; a consumer that freezes updates while closed and reopens with changed content will
        /// glide the open instead of snapping.</summary>
        public float HeightGlideSeconds = 0f;

        /// <summary>Optional dimmed backdrop behind the panel. When set, <see cref="Update(Pointer,InputState)"/> reserves it on the
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

        float _glideHeight;
        bool _glideInitialized;

        public float ScrollOffset { get; private set; }
        public float Stride => ItemHeight + ItemSpacing;
        public float ContentHeight => ItemCount * Stride;

        /// <summary>The un-glided target height this frame: <see cref="Bounds"/>'s height, or the drag-resize
        /// height clamped to <see cref="MinHeight"/>/<see cref="MaxHeight"/> when <see cref="Resizable"/>. This is
        /// what <see cref="EffectiveHeight"/> equals when <see cref="HeightGlideSeconds"/> is off, and what it
        /// glides toward when the glide is on.</summary>
        float TargetHeight => Resizable
            ? Math.Clamp(_resizeHeight ?? Bounds.Height, MinHeight, MaxHeight)
            : Bounds.Height;

        /// <summary>The panel's height this frame after any drag-resize and the opt-in height glide (docked to
        /// <see cref="Bounds"/>'s bottom edge). Equals <see cref="TargetHeight"/> exactly (no state, no lag) unless
        /// <see cref="HeightGlideSeconds"/> is &gt; 0 AND a dt-fed <see cref="Update(Pointer,InputState,float)"/>
        /// call has already run at least once - see <see cref="HeightGlideSeconds"/> for the full contract.</summary>
        public float EffectiveHeight => HeightGlideSeconds > 0f && _glideInitialized ? _glideHeight : TargetHeight;

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

        /// <summary>Apply wheel + drag scrolling for this frame, drive header-resize + scrim, and (optionally)
        /// reserve the pointer. Feeds no dt, so <see cref="HeightGlideSeconds"/> never glides on this path -
        /// <see cref="EffectiveHeight"/> snaps to target exactly as before that knob existed. Use
        /// <see cref="Update(Pointer,InputState,float)"/> to opt into the glide.</summary>
        public void Update(Pointer pointer, InputState input) => Update(pointer, input, 0f);

        /// <summary>Apply wheel + drag scrolling for this frame, drive header-resize + scrim + the opt-in height
        /// glide (<see cref="HeightGlideSeconds"/>), and (optionally) reserve the pointer. <paramref name="dt"/> is
        /// the frame delta in seconds; a value &lt;= 0 (including the legacy <see cref="Update(Pointer,InputState)"/>
        /// overload, which forwards 0) never advances or initializes the glide, so <see cref="EffectiveHeight"/>
        /// stays exactly at <c>TargetHeight</c> until a positive dt is fed at least once.</summary>
        public void Update(Pointer pointer, InputState input, float dt)
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

            AdvanceHeightGlide(dt);

            Rect content = ContentBounds;

            if (pointer.IsPointerIn(content) && input.ScrollDelta != 0f)
                ScrollOffset -= input.ScrollDelta * WheelSpeed;

            // Drag-pan only when the drag began in the content region AND we are not mid-resize. The `_resizing`
            // guard is load-bearing: a header drag that grows the panel moves ContentBounds up past the fixed
            // press-origin, so GetDragDelta's press-origin test would start returning the drag delta and pan the
            // list while the user is only resizing. Latching on _resizing keeps resize and scroll mutually exclusive.
            float dragY = _resizing || !DragScrollingEnabled ? 0f : pointer.GetDragDelta(content).Y;
            if (dragY != 0f)
                ScrollOffset -= dragY;

            ScrollOffset = Math.Clamp(ScrollOffset, 0f, MaxScroll);
        }

        /// <summary>Advance the opt-in height glide by one frame. A <paramref name="dt"/> &lt;= 0 (the legacy
        /// no-dt <see cref="Update(Pointer,InputState)"/> overload, or a genuinely paused frame) leaves the glide
        /// state untouched entirely, so a caller that never feeds a positive dt never initializes it and
        /// <see cref="EffectiveHeight"/> keeps returning <see cref="TargetHeight"/> directly. Snaps (no easing) on
        /// the first initializing call, whenever the panel is fully hidden, and while a drag-resize is in
        /// progress, per the <see cref="HeightGlideSeconds"/> contract.</summary>
        void AdvanceHeightGlide(float dt)
        {
            if (HeightGlideSeconds <= 0f)
            {
                // Re-arm: if the glide is switched back on later, it snaps fresh instead of resuming from a
                // value that went stale while the feature was off.
                _glideInitialized = false;
                return;
            }

            if (dt <= 0f) return;   // no positive dt fed yet - leave state untouched (see the doc comment above)

            float target = TargetHeight;

            if (!_glideInitialized || TransitionAlpha <= 0f || _resizing)
            {
                _glideHeight = target;
                _glideInitialized = true;
                return;
            }

            float next = _glideHeight + (target - _glideHeight) * (1f - MathF.Exp(-dt / HeightGlideSeconds));
            _glideHeight = MathF.Abs(target - next) <= 0.5f ? target : next;
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

        /// <summary>Obsolete: pass a <see cref="LocalizedText"/>. A raw string title bypasses localization.</summary>
        [Obsolete("Pass a LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public void DrawHeader(SpriteBatch batch, Texture2D white, SpriteFont font, string title) =>
            DrawHeader(batch, white, font, LocalizedText.Raw(title));

        /// <summary>Draw the header band: optional <see cref="HeaderBackground"/> fill, left-aligned <paramref name="title"/>,
        /// and a bottom divider. No-op when <see cref="HeaderHeight"/> is 0. The title is resolved against the
        /// ambient catalog on every draw, so a runtime locale switch takes effect on the next frame.</summary>
        public void DrawHeader(SpriteBatch batch, Texture2D white, SpriteFont font, LocalizedText title)
        {
            if (HeaderHeight <= 0f) return;
            Rect h = HeaderBounds;
            if (HeaderBackground.W > 0f) GuiDraw.Fill(batch, white, h, HeaderBackground);
            string resolved = title.Resolve();
            if (!string.IsNullOrEmpty(resolved))
            {
                float ty = h.Y + (HeaderHeight - font.LineHeight) * 0.5f;
                batch.DrawString(font, resolved, new Vector2(MathF.Floor(h.X + 8f), MathF.Floor(ty)), (Color)HeaderTextColor);
            }
            GuiDraw.Fill(batch, white, new Rect(h.X, h.Bottom - 1f, h.Width, 1f), HeaderDivider);
        }

        /// <summary>Flush pending draws and clip subsequent draws to <see cref="ContentBounds"/>. Draw items, then call <see cref="EndClip"/>.</summary>
        public void BeginClip(SpriteBatch batch) => batch.SetScissor(ContentBounds);

        /// <summary>Flush the clipped draws and restore the full viewport.</summary>
        public void EndClip(SpriteBatch batch) => batch.ClearScissor();
    }
}
