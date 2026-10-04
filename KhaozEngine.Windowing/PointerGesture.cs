using System;
using System.Numerics;

namespace KhaozEngine.Windowing
{
    /// <summary>What one mouse button is doing right now, per <see cref="PointerGesture"/>.</summary>
    public enum PointerGesturePhase
    {
        /// <summary>The button is up, or the press was ruled inert (it began while blocked).</summary>
        Idle,
        /// <summary>Pressed, but the cursor has not yet travelled past the threshold, so it is still undecided
        /// whether this press is a tap or a drag. Nothing drags while a press is here.</summary>
        Pending,
        /// <summary>The press crossed the threshold. It drags from here until the button comes up or a block arrives,
        /// however still the cursor goes, and it can never become a tap.</summary>
        Dragging,
    }

    /// <summary>
    /// Splits one mouse button into a TAP and a DRAG. A press is undecided until the cursor has travelled past the
    /// threshold, and once it has, it is a drag until the button comes up. A release while undecided is the tap.
    /// </summary>
    /// <remarks>
    /// Pure and headless: it reads its own button, the cursor position and the mouse delta from the frame's
    /// <see cref="InputState"/> and owns no camera and no input source.
    /// <para>By default, travel is path length accumulated across the whole press, not this frame's step, so three slow points
    /// a frame still becomes a drag rather than never deciding, and a wiggle that returns to its start is a drag
    /// rather than a tap.</para>
    /// <para>Crossing the threshold REPLAYS the travel accumulated while undecided rather than starting clean. The
    /// alternative is a dead zone: the first few points of every drag would be swallowed and whatever the drag
    /// drives would start from behind the cursor for the rest of the press. Replaying costs one frame of catch-up
    /// worth at most the threshold and keeps the property that the same mouse path ends in the same place
    /// whatever the threshold is.</para>
    /// <para>Above a zero threshold, a press that begins while blocked (the UI owns the pointer, or the window is
    /// unfocused) is inert for its whole life. It never drags, and its release is never a tap. A block that arrives
    /// mid-press stops the drag and makes the rest of that press inert too. At a zero or negative threshold there is
    /// no inert press: a block stops the drag, and a button still held drags again on the first unblocked
    /// frame.</para>
    /// <para>A <see cref="PointerTapTolerance"/> replaces the path rule with straight-line distance from the press
    /// point and a time grace, so a click that wobbles and comes back stays a tap. It needs frame time, so a gesture
    /// built on one advances through the overload that takes <c>elapsedSeconds</c>.</para>
    /// <para>Both <c>Advance</c> overloads allocate nothing.</para>
    /// </remarks>
    /// <param name="button">The button this gesture watches. Every other button is ignored.</param>
    /// <param name="thresholdPixels">Accumulated <see cref="InputState.MouseDeltaPoints"/> travel that turns a
    /// press into a drag, in window points before and during capture. The parameter name is retained for
    /// compatibility. Four is about the width of the hand tremor in a click. Zero or less means a press drags
    /// from its first frame and never taps.</param>
    public sealed class PointerGesture(MouseButton button, float thresholdPixels = 4f)
    {
        Vector2 _pressPosition;     // where the live press began, promoted to TapPosition if it ends as a tap
        Vector2 _pendingDelta;      // net displacement accumulated while undecided, replayed on the crossing frame
        float _pendingTravel;       // PATH LENGTH accumulated while undecided, which is what the threshold tests
        float _heldSeconds;         // time since the press frame, which picks the tolerance limit
        bool _pastGrace;            // a held frame of this press has already been judged at or past the grace
        bool _wasDown;              // last frame's button state, so the press and release edges need no edge sets
        bool _suppressed;           // this press began while blocked: inert for its whole life, no drag, no tap

        /// <summary>Builds a gesture whose tap rule is <paramref name="tapTolerance"/> rather than the path length.
        /// Its <see cref="ThresholdPixels"/> is <see cref="PointerTapTolerance.DistancePoints"/>. Throws
        /// <see cref="ArgumentException"/> when the tolerance is out of range.</summary>
        public PointerGesture(MouseButton button, PointerTapTolerance tapTolerance)
            : this(button, Validated(tapTolerance).DistancePoints)
        {
            TapTolerance = tapTolerance;
        }

        /// <summary>The button this gesture watches.</summary>
        public MouseButton Button { get; } = button;

        /// <summary>The tap rule this instance was built on, or null for the strict path-length rule.</summary>
        public PointerTapTolerance? TapTolerance { get; }

        /// <summary>The threshold this instance was built on, in window points. The property name is retained for
        /// compatibility. Zero or less means drag from the first frame and never tap.</summary>
        public float ThresholdPixels { get; } = thresholdPixels;

        /// <summary>What the button is doing after the most recent <c>Advance</c>.</summary>
        public PointerGesturePhase Phase { get; private set; }

        /// <summary>The delta a drag should apply for the frame of the most recent <c>Advance</c>: zero
        /// unless dragging, that frame's own <see cref="InputState.MouseDeltaPoints"/> while dragging, and the whole
        /// accumulated travel on the frame the threshold is crossed (the catch-up). With a <see cref="TapTolerance"/>
        /// the catch-up is the net displacement, capped by <see cref="PointerTapTolerance.CatchUpLimitPoints"/> when the
        /// end of the grace decided the press.</summary>
        public Vector2 DragDelta { get; private set; }

        /// <summary>
        /// True when the most recent <c>Advance</c> saw the release of a press that never became a drag.
        /// It describes that frame only. Nothing clears it except the next <c>Advance</c>, so a caller that
        /// reads it on a frame it did not advance sees the previous frame's tap again. Read it only after
        /// advancing with the snapshot you are acting on.
        /// </summary>
        public bool TapThisFrame { get; private set; }

        /// <summary>Where the press began, in <see cref="InputState.MousePosition"/> space. Meaningful on a frame
        /// where <see cref="TapThisFrame"/> is true, and left on the last tap's origin otherwise.</summary>
        public Vector2 TapPosition { get; private set; }

        /// <summary>Advance one frame.</summary>
        /// <param name="input">This frame's snapshot. The gesture reads its own button's down state,
        /// <see cref="InputState.MousePosition"/> as the press origin, <see cref="InputState.MouseDeltaPoints"/> as the
        /// travel, and <see cref="InputState.WindowFocused"/>, whose loss counts as blocked because the input
        /// accumulator releases every held button on unfocus and that would otherwise look like a tap.</param>
        /// <param name="uiBlocked">Whether the UI owns the pointer this frame. A press that begins while blocked is
        /// inert for its whole life.</param>
        /// <exception cref="InvalidOperationException">The gesture has a <see cref="TapTolerance"/>, whose grace needs
        /// the frame time.</exception>
        public void Advance(in InputState input, bool uiBlocked)
        {
            if (TapTolerance is not null)
            {
                throw new InvalidOperationException(
                    "A gesture with a tap tolerance needs the frame time. Call Advance(input, uiBlocked, elapsedSeconds).");
            }

            Step(input, uiBlocked, 0f);
        }

        /// <summary>Advance one frame that lasted <paramref name="elapsedSeconds"/>, which the
        /// <see cref="TapTolerance"/> grace counts. Without a tolerance the time is neither used nor checked and this
        /// is exactly <see cref="Advance(in InputState, bool)"/>, whatever the value.</summary>
        /// <param name="input">This frame's snapshot, read as in <see cref="Advance(in InputState, bool)"/>.</param>
        /// <param name="uiBlocked">Whether the UI owns the pointer this frame.</param>
        /// <param name="elapsedSeconds">This frame's duration. With a tolerance, finite and not negative.</param>
        /// <exception cref="ArgumentOutOfRangeException">The gesture has a <see cref="TapTolerance"/> and
        /// <paramref name="elapsedSeconds"/> is NaN, infinite or negative.</exception>
        public void Advance(in InputState input, bool uiBlocked, float elapsedSeconds)
        {
            if (TapTolerance is not null && (!float.IsFinite(elapsedSeconds) || elapsedSeconds < 0f))
            {
                throw new ArgumentOutOfRangeException(nameof(elapsedSeconds), elapsedSeconds, "Must be finite and not negative.");
            }

            Step(input, uiBlocked, elapsedSeconds);
        }

        void Step(in InputState input, bool uiBlocked, float elapsedSeconds)
        {
            bool down = input.IsDown(Button);
            bool blocked = uiBlocked || !input.WindowFocused;
            TapThisFrame = false;

            if (ThresholdPixels <= 0f)
            {
                // No undecided phase means no tap can ever fire and the drag starts on the first frame of the
                // press, including resuming mid-hold once a block lifts.
                Phase = down && !blocked ? PointerGesturePhase.Dragging : PointerGesturePhase.Idle;
                DragDelta = Phase == PointerGesturePhase.Dragging ? input.MouseDeltaPoints : Vector2.Zero;
                _wasDown = down;
                return;
            }

            if (blocked)
            {
                // Latched on every blocked frame rather than on the press edge alone, because a caller may not
                // advance on every blocked frame and so cannot be relied on to have seen the edge.
                _suppressed = down;
                Reset(down, PointerGesturePhase.Idle);
                return;
            }

            if (!down)
            {
                if (Phase == PointerGesturePhase.Pending)
                {
                    TapThisFrame = true;
                    TapPosition = _pressPosition;
                }
                _suppressed = false;
                Reset(false, PointerGesturePhase.Idle);
                return;
            }

            if (_suppressed)
            {
                Reset(true, PointerGesturePhase.Idle);
                return;
            }

            if (!_wasDown)
            {
                _wasDown = true;
                Phase = PointerGesturePhase.Pending;
                _pressPosition = input.MousePosition;
                _pendingDelta = Vector2.Zero;
                _pendingTravel = 0f;
                _heldSeconds = 0f;
                _pastGrace = false;
            }
            else
            {
                _heldSeconds += elapsedSeconds;
            }

            Vector2 mouseDelta = input.MouseDeltaPoints;
            if (Phase == PointerGesturePhase.Dragging)
            {
                DragDelta = mouseDelta;     // decided: every frame passes straight through until the button comes up
                return;
            }

            _pendingDelta += mouseDelta;
            _pendingTravel += mouseDelta.Length();
            bool graceDecided = false;
            if (TapTolerance is { } tolerance)
            {
                bool pastGrace = _heldSeconds >= tolerance.GraceSeconds;
                graceDecided = pastGrace && !_pastGrace;
                _pastGrace = pastGrace;
                if (_pendingDelta.Length() <= tolerance.LimitAt(_heldSeconds))
                {
                    DragDelta = Vector2.Zero;
                    return;
                }
            }
            else if (_pendingTravel <= ThresholdPixels)
            {
                DragDelta = Vector2.Zero;   // still a candidate tap, so nothing may move yet
                return;
            }

            Phase = PointerGesturePhase.Dragging;
            // Catch up in one frame, so the drag has no dead zone at its start. When the grace ending decided the
            // press, the tolerance may cap that step, because the press may have gathered up to the grace distance.
            DragDelta = graceDecided ? TapTolerance!.CapCatchUp(_pendingDelta) : _pendingDelta;
            _pendingDelta = Vector2.Zero;
        }

        static PointerTapTolerance Validated(PointerTapTolerance tapTolerance)
        {
            ArgumentNullException.ThrowIfNull(tapTolerance);
            tapTolerance.Validate(nameof(tapTolerance));
            return tapTolerance;
        }

        void Reset(bool down, PointerGesturePhase phase)
        {
            Phase = phase;
            DragDelta = Vector2.Zero;
            _pendingDelta = Vector2.Zero;
            _pendingTravel = 0f;
            _wasDown = down;
        }
    }
}
