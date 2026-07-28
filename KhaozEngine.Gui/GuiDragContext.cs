using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// Paints the drag ghost into <paramref name="rect"/> (the rect <see cref="GuiDragContext.GhostRect"/> resolved
    /// for this frame). Supplied by the drag SOURCE at grab time, so the Gui layer never learns what is being
    /// dragged: it only knows how to put this delegate under the pointer. Mirrors
    /// <see cref="SlotGrid.DrawSlotContent"/>'s discipline, and takes the same
    /// <paramref name="white"/> / <paramref name="font"/> a widget's own <c>Draw</c> receives.
    /// </summary>
    public delegate void DragGhostPainter(SpriteBatch batch, Texture2D white, SpriteFont? font, Rect rect);

    /// <summary>
    /// What is being dragged. <see cref="Token"/> is an OPAQUE, game-supplied object the Gui layer never inspects
    /// (the same discipline <see cref="SlotContent"/> holds: the widget knows nothing about game items), and
    /// <see cref="SourceId"/> / <see cref="SourceIndex"/> identify where it was picked up so a drop handler can tell
    /// "same grid, different slot" from "a different widget entirely". A readonly value: the source builds one at
    /// grab time and hands it to <see cref="GuiDragContext.Begin"/>.
    /// </summary>
    public readonly struct DragPayload
    {
        /// <summary>The game's own object for whatever is being dragged (an item id, an inventory entry, an ability
        /// handle). Never read by the engine, only carried and handed back on the drop. May be null when
        /// <see cref="SourceId"/> plus <see cref="SourceIndex"/> already identify the thing.</summary>
        public object? Token { get; }

        /// <summary>Opaque identity of the widget the drag started in, for the drop handler to compare by reference
        /// (the widgets pass <c>this</c>). Null when the source is not a widget.</summary>
        public object? SourceId { get; }

        /// <summary>Index within the source widget (a slot index, a row index), or -1 when the source has no
        /// per-index addressing.</summary>
        public int SourceIndex { get; }

        /// <summary>How to draw the thing under the pointer while it is in flight. Null draws the built-in themed
        /// placeholder frame instead (<see cref="GuiDragContext.GhostColor"/>).</summary>
        public DragGhostPainter? Ghost { get; }

        /// <summary>Build a payload: an opaque <paramref name="token"/>, the source identity, and an optional ghost painter.</summary>
        public DragPayload(object? token, object? sourceId = null, int sourceIndex = -1, DragGhostPainter? ghost = null)
        {
            Token = token;
            SourceId = sourceId;
            SourceIndex = sourceIndex;
            Ghost = ghost;
        }

        /// <summary>This payload with <paramref name="ghost"/> as its painter (everything else unchanged). Lets a
        /// source widget supply a default ghost for a payload the game built without one.</summary>
        public DragPayload WithGhost(DragGhostPainter? ghost) => new(Token, SourceId, SourceIndex, ghost);
    }

    /// <summary>
    /// A committed drop: the <see cref="Payload"/> that was carried, and the target that accepted it. Read from
    /// <see cref="GuiDragContext.LastDrop"/> on the frame <see cref="GuiDragContext.WasDropped"/> is true, or
    /// received through <see cref="GuiDragContext.OnDropped"/>.
    /// </summary>
    public readonly struct DragDropResult
    {
        /// <summary>What was dropped.</summary>
        public DragPayload Payload { get; }
        /// <summary>Opaque identity of the accepting target (a widget passes <c>this</c>), or null for a bare rect target.</summary>
        public object? TargetId { get; }
        /// <summary>Index within the target widget (a slot index), or -1 when the target has no per-index addressing.</summary>
        public int TargetIndex { get; }

        /// <summary>Build a drop result. Constructed by <see cref="GuiDragContext"/>; public so tests and hosts can synthesize one.</summary>
        public DragDropResult(in DragPayload payload, object? targetId, int targetIndex)
        {
            Payload = payload;
            TargetId = targetId;
            TargetIndex = targetIndex;
        }
    }

    /// <summary>
    /// The drag-and-drop session that spans widgets: one live drag at a time, shared by every widget that takes
    /// part. This is deliberately NOT a widget base class - a drag is not the state of any one widget, it is the
    /// state BETWEEN two of them, so it lives in its own object the participating widgets consult (and the Gui
    /// widgets are independent sealed classes with no common base to hang it off).
    ///
    /// <para>Per frame, in order: <see cref="BeginFrame"/> once before your widget updates, then the widgets
    /// (sources call <see cref="ShouldBeginDrag"/> + <see cref="Begin"/>, targets call <see cref="OfferTarget"/>),
    /// then <see cref="EndFrame"/> once after them, then <see cref="Draw"/> on top of your UI.
    /// <see cref="EndFrame"/> is what turns a release over nothing into a cancel: "no target offered" is only
    /// knowable once every widget has had its turn.</para>
    ///
    /// <para>A target accepts or refuses BEFORE the release: it passes its own verdict to
    /// <see cref="OfferTarget"/> every frame the drag hovers it, so the ghost can show the refusal
    /// (<see cref="ShowRejectOverlay"/>) and the drop simply never commits, instead of committing and being undone.
    /// When two targets overlap, the FIRST offer of the frame wins, matching <c>ScreenStack</c>'s top-to-bottom
    /// input routing - including when the topmost one refuses.</para>
    ///
    /// <para>Grabbing the drag calls <see cref="Pointer.ConsumeGesture"/>, so the release that drops an item cannot
    /// also register as a tap on whatever sits under it.</para>
    /// </summary>
    public sealed class GuiDragContext
    {
        DragPayload _payload;
        Rect _sourceBounds;
        Vector2 _pointer;
        bool _dragging;
        bool _released;   // the pointer was released THIS frame (captured in BeginFrame)

        // Per-frame drop-target state, cleared by BeginFrame. `_offered` latches on the first OfferTarget of the
        // frame so a lower widget cannot steal a target the topmost one already claimed (or already refused).
        bool _offered, _offerAccepted;
        object? _offerId;
        int _offerIndex = -1;

        // The return animation: a cancelled drag flies the ghost back to the source rect it was grabbed from,
        // rather than vanishing. `_returning` is a purely cosmetic tail - the drag itself is already over.
        bool _returning;
        float _returnT;
        Vector2 _returnFrom;

        /// <summary>Pixels the pointer must travel from the press origin before a held press becomes a drag rather
        /// than a tap. The one arm rule every source shares (<see cref="ShouldBeginDrag"/>). Default 6, matching
        /// <see cref="TreeView.DragThreshold"/>.</summary>
        public float DragThreshold { get; set; } = 6f;

        /// <summary>Seconds the ghost takes to fly back to its source rect after a cancelled or refused drop.
        /// Default 0.12. Set to 0 to skip the animation entirely (the ghost just disappears): the return is a
        /// cosmetic tail and nothing is ever load-bearing on it.</summary>
        public float ReturnDuration { get; set; } = 0.12f;

        /// <summary>Ghost size as a multiple of the source rect it was grabbed from (default 1: same size as the
        /// slot it came out of). Above 1 gives the "lifted" look some inventories use.</summary>
        public float GhostScale { get; set; } = 1f;

        /// <summary>Alpha multiplier for the engine-drawn parts of the ghost: the built-in placeholder frame and the
        /// reject overlay. A caller-supplied <see cref="DragPayload.Ghost"/> paints itself and owns its own alpha.</summary>
        public float GhostOpacity { get; set; } = 0.85f;

        /// <summary>Fill of the built-in placeholder ghost drawn when the payload carries no <see cref="DragPayload.Ghost"/>.</summary>
        public Vector4 GhostColor = GuiTheme.Default.SurfaceHover;
        /// <summary>Border of the built-in placeholder ghost.</summary>
        public Vector4 GhostBorderColor = GuiTheme.Default.BorderHover;
        /// <summary>Translucent wash drawn over the ghost while it is NOT over an accepting target, so a refusal
        /// reads before the player lets go.</summary>
        public Vector4 RejectTint = new(GuiTheme.Default.Danger.X, GuiTheme.Default.Danger.Y, GuiTheme.Default.Danger.Z, 0.35f);
        /// <summary>Look knobs (corners / shadow / glow) for the built-in placeholder ghost.</summary>
        public GuiStyle Style = GuiStyle.Default;

        /// <summary>True while something is being carried (between <see cref="Begin"/> and the drop / cancel).</summary>
        public bool IsDragging => _dragging;
        /// <summary>True while the cosmetic return animation is playing after a cancelled or refused drop.</summary>
        public bool IsReturning => _returning;
        /// <summary>True while there is anything to draw: a live drag or its return animation.</summary>
        public bool IsActive => _dragging || _returning;

        /// <summary>What is being carried (valid while <see cref="IsActive"/>).</summary>
        public DragPayload Payload => _payload;
        /// <summary>The rect the drag was grabbed from: the ghost's size source and where a cancel returns to.</summary>
        public Rect SourceBounds => _sourceBounds;
        /// <summary>Pointer position as of the last <see cref="BeginFrame"/>.</summary>
        public Vector2 PointerPosition => _pointer;

        /// <summary>True when some target has offered itself under the pointer this frame (accepting or not).</summary>
        public bool IsOverTarget => _offered;
        /// <summary>True when the target under the pointer this frame will take the payload. Drives the ghost's accept / reject look.</summary>
        public bool IsOverAcceptingTarget => _offered && _offerAccepted;
        /// <summary>Identity of the target that claimed the pointer this frame, or null.</summary>
        public object? HoveredTargetId => _offered ? _offerId : null;
        /// <summary>Index within the target that claimed the pointer this frame, or -1.</summary>
        public int HoveredTargetIndex => _offered ? _offerIndex : -1;
        /// <summary>True while a live drag is over nothing that will take it (what <see cref="Draw"/> washes with <see cref="RejectTint"/>).</summary>
        public bool ShowRejectOverlay => _dragging && !IsOverAcceptingTarget;

        /// <summary>True on the frame a drop committed on an accepting target. Cleared by the next <see cref="BeginFrame"/>.</summary>
        public bool WasDropped { get; private set; }
        /// <summary>The drop that committed (valid on the frame <see cref="WasDropped"/> is true).</summary>
        public DragDropResult LastDrop { get; private set; }
        /// <summary>True on the frame a drag ended WITHOUT a drop: released over nothing that would take it, or
        /// <see cref="Cancel"/>ed. Cleared by the next <see cref="BeginFrame"/>.</summary>
        public bool WasCancelled { get; private set; }
        /// <summary>What was being carried when the drag was cancelled (valid on the frame <see cref="WasCancelled"/> is true).</summary>
        public DragPayload CancelledPayload { get; private set; }

        /// <summary>Fired when a drop commits, before <see cref="WasDropped"/> is polled.</summary>
        public Action<DragDropResult>? OnDropped;
        /// <summary>Fired when a drag ends without a drop. A game that wants "drag it out of the panel to discard
        /// it" hangs off this, and should gate the destructive half behind its own confirmation: a release over
        /// empty space is easy to do by accident.</summary>
        public Action<DragPayload>? OnCancelled;

        /// <summary>
        /// Start the frame: sample the pointer, advance the return animation by <paramref name="dt"/> seconds, and
        /// clear the per-frame target and result state. Call once, before any widget update that takes part in the
        /// drag.
        /// </summary>
        public void BeginFrame(Pointer pointer, float dt)
        {
            ArgumentNullException.ThrowIfNull(pointer);
            _pointer = pointer.Position;
            _released = pointer.IsJustReleased;

            _offered = false;
            _offerAccepted = false;
            _offerId = null;
            _offerIndex = -1;

            WasDropped = false;
            WasCancelled = false;

            if (_returning)
            {
                _returnT += ReturnDuration > 0f ? dt / ReturnDuration : 1f;
                if (_returnT >= 1f) EndReturn();
            }
        }

        /// <summary>
        /// The shared arm rule: true once a held press that BEGAN inside <paramref name="sourceBounds"/> has
        /// travelled <see cref="DragThreshold"/>. Built on <see cref="Pointer.IsDragStartIn"/>, so the press-origin
        /// invariant holds and the gesture keeps its grip after the cursor leaves that rect - which is exactly what
        /// a per-frame containment test (<see cref="Pointer.IsPressingIn"/>) cannot do. Below the threshold the
        /// gesture is still a plain tap.
        /// </summary>
        public bool ShouldBeginDrag(Pointer pointer, Rect sourceBounds)
        {
            ArgumentNullException.ThrowIfNull(pointer);
            if (_dragging || pointer.IsConsumed || !pointer.IsDragStartIn(sourceBounds)) return false;
            return (pointer.Position - pointer.PressOrigin).Length() >= DragThreshold;
        }

        /// <summary>
        /// Grab <paramref name="payload"/>, sized and returned-to by <paramref name="sourceBounds"/>. Consumes the
        /// pointer gesture (<see cref="Pointer.ConsumeGesture"/>) so the release that drops it cannot also tap
        /// whatever is underneath. Returns false when a drag is already live.
        /// </summary>
        public bool Begin(Pointer pointer, in DragPayload payload, Rect sourceBounds)
        {
            ArgumentNullException.ThrowIfNull(pointer);
            if (_dragging) return false;
            _payload = payload;
            _sourceBounds = sourceBounds;
            _dragging = true;
            _returning = false;
            _returnT = 0f;
            pointer.ConsumeGesture();
            return true;
        }

        /// <summary>
        /// Offer this widget as the drop target under the pointer, with its verdict on the live payload. Call it
        /// only when the pointer is actually over the target region (the widget owns its own geometry); the
        /// rect-testing convenience is <see cref="OfferTargetIn"/>.
        /// <para>The first offer of the frame wins and later ones are ignored, so update your widgets in the same
        /// top-to-bottom order you route input. Returns true on the single frame the drop COMMITS here: this offer
        /// claimed the pointer, <paramref name="accepted"/> was true, and the button was released this frame.</para>
        /// </summary>
        public bool OfferTarget(object? targetId, int targetIndex, bool accepted)
        {
            if (!_dragging || _offered) return false;

            _offered = true;
            _offerAccepted = accepted;
            _offerId = targetId;
            _offerIndex = targetIndex;

            if (!accepted || !_released) return false;

            LastDrop = new DragDropResult(_payload, targetId, targetIndex);
            WasDropped = true;
            _dragging = false;
            _payload = default;
            OnDropped?.Invoke(LastDrop);
            return true;
        }

        /// <summary>
        /// <see cref="OfferTarget"/> with the hit-test done for you against <paramref name="bounds"/>: the one-line
        /// way to make a bare rect (a trash zone, a "drop here to discard" panel) a drop target without a widget.
        /// </summary>
        public bool OfferTargetIn(Rect bounds, object? targetId, bool accepted, int targetIndex = -1) =>
            _dragging && bounds.Contains(_pointer) && OfferTarget(targetId, targetIndex, accepted);

        /// <summary>
        /// Abandon the live drag with no drop (the Escape-key path, or a host tearing the UI down mid-gesture).
        /// Fires <see cref="OnCancelled"/> and starts the return animation, exactly like releasing over nothing.
        /// No-op when nothing is being dragged.
        /// </summary>
        public void Cancel()
        {
            if (_dragging) EndWithoutDrop();
        }

        /// <summary>
        /// Close the frame: a release with no accepting target under it becomes a cancel. Call once, after every
        /// widget that takes part has updated - that is the earliest moment "nothing would take it" is known.
        /// </summary>
        public void EndFrame()
        {
            if (_dragging && _released) EndWithoutDrop();
        }

        /// <summary>
        /// Where the ghost is this frame: a <see cref="SourceBounds"/>-sized (times <see cref="GhostScale"/>) rect
        /// centred on the pointer, or on the eased return path while <see cref="IsReturning"/>. Public so a host can
        /// place its own decoration against it and so the ghost is assertable headlessly.
        /// </summary>
        public Rect GhostRect
        {
            get
            {
                float w = _sourceBounds.Width * GhostScale;
                float h = _sourceBounds.Height * GhostScale;
                Vector2 c = _returning ? Vector2.Lerp(_returnFrom, SourceCenter, Smoothstep(_returnT)) : _pointer;
                return new Rect(c.X - w * 0.5f, c.Y - h * 0.5f, w, h);
            }
        }

        /// <summary>
        /// Draw the ghost at <see cref="GhostRect"/>: the payload's own <see cref="DragPayload.Ghost"/> painter when
        /// it has one, else a themed placeholder frame, plus the <see cref="RejectTint"/> wash while the drag is
        /// over nothing that will take it. Draw this LAST, on top of the UI it floats over. No-op when nothing is
        /// active.
        /// </summary>
        public void Draw(SpriteBatch batch, Texture2D white, SpriteFont? font = null)
        {
            if (!IsActive) return;
            Rect r = GhostRect;

            if (_payload.Ghost is { } paint) paint(batch, white, font, r);
            else
                GuiDraw.FillStyled(batch, white, r, Style,
                    GuiDraw.WithOpacity(GhostColor, GhostOpacity), GuiDraw.WithOpacity(GhostBorderColor, GhostOpacity));

            if (ShowRejectOverlay) GuiDraw.Fill(batch, white, r, GuiDraw.WithOpacity(RejectTint, GhostOpacity));
        }

        Vector2 SourceCenter => new(_sourceBounds.X + _sourceBounds.Width * 0.5f, _sourceBounds.Y + _sourceBounds.Height * 0.5f);

        // The drag is over the moment it ends: `_dragging` drops immediately so no target can still claim it, and
        // the return animation runs afterwards purely as a visual (IsReturning, never IsDragging). With
        // ReturnDuration at 0 there is no tail at all and the payload is dropped on the spot.
        void EndWithoutDrop()
        {
            _dragging = false;
            WasCancelled = true;
            CancelledPayload = _payload;
            _returnFrom = new Vector2(GhostRect.X + GhostRect.Width * 0.5f, GhostRect.Y + GhostRect.Height * 0.5f);

            if (ReturnDuration > 0f) { _returning = true; _returnT = 0f; }
            else _payload = default;

            OnCancelled?.Invoke(CancelledPayload);
        }

        void EndReturn()
        {
            _returning = false;
            _returnT = 0f;
            _payload = default;
        }

        // Smoothstep ease so the ghost leaves and lands softly instead of snapping linearly.
        static float Smoothstep(float t)
        {
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            return t * t * (3f - 2f * t);
        }
    }
}
