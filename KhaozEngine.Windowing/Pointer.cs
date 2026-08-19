using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// Bounds-aware pointer over the mouse, with the press-origin click-through invariant. Feed it the
    /// frame's <see cref="InputState"/> via <see cref="Update(KhaozEngine.Windowing.InputState)"/>; then hit-test with the bounds helpers
    /// (<see cref="IsTapIn"/>, <see cref="IsPressingIn"/>, ...) rather than raw position + button checks.
    /// Ported from the MonoGame <c>InputManager</c> core (desktop/mouse; touch/gamepad are follow-ups; pinch/swipe
    /// gestures live in <c>PinchRecognizer</c>/<c>GestureRecognizer</c>).
    /// <para>A tap whose press and release both land inside ONE frame still registers, as a release with the
    /// press-origin at the cursor, provided the snapshot's <see cref="InputState.MousePressed"/> carries the
    /// press edge. A producer that leaves that set empty is read exactly as before, so the only thing it loses
    /// is the same-frame tap.</para>
    /// </summary>
    public sealed class Pointer
    {
        readonly List<Rect> _blocked = new();
        bool _down, _wasDown, _mid, _wasMid, _right, _wasRight;
        bool _consumed;   // current LEFT gesture claimed by a consumer; reset on the next fresh left press
        bool _rightConsumed;   // the same latch for the RIGHT gesture, tracked separately (see ConsumeRightGesture)
        bool _focused = true;   // OS window focus, from InputState.WindowFocused; windows start focused
        Vector2 _pos, _prevPos, _pressOrigin, _rightPressOrigin;

        /// <summary>Current pointer position (pixels).</summary>
        public Vector2 Position => _pos;
        /// <summary>Where the current press began (valid while down and on the release frame).</summary>
        public Vector2 PressOrigin => _pressOrigin;
        /// <summary>Where the current RIGHT press began (valid while the right button is down and on its release
        /// frame). Latched independently of <see cref="PressOrigin"/>, so a right gesture keeps its own origin even
        /// when a left press happens during it.</summary>
        public Vector2 RightPressOrigin => _rightPressOrigin;
        /// <summary>Movement since the previous frame.</summary>
        public Vector2 Delta => _pos - _prevPos;

        public bool IsDown => _down;
        public bool IsJustPressed => _down && !_wasDown;
        /// <summary>True on the frame the left button went up. Also true on a frame whose snapshot reports a
        /// press for a button that is already up again (a tap whose press and release both landed inside one
        /// frame), where <see cref="IsJustPressed"/> never fires because the button was never observed down.
        /// A tap is a press plus a release, so the release edge is the one the tap queries key on.</summary>
        public bool IsJustReleased => !_down && _wasDown;
        public bool IsMiddleDown => _mid;
        public bool IsMiddleJustPressed => _mid && !_wasMid;
        /// <summary>The middle button's <see cref="IsJustReleased"/>, same-frame tap included.</summary>
        public bool IsMiddleJustReleased => !_mid && _wasMid;
        public bool IsRightDown => _right;
        public bool IsRightJustPressed => _right && !_wasRight;
        /// <summary>The right button's <see cref="IsJustReleased"/>, same-frame tap included.</summary>
        public bool IsRightJustReleased => !_right && _wasRight;

        /// <summary>
        /// True while the owning window has OS focus (from <see cref="InputState.WindowFocused"/>; <c>true</c> until
        /// the first <see cref="Update(KhaozEngine.Windowing.InputState)"/>). The hover query <see cref="IsHoveringIn"/> is gated on this so a
        /// background window reports no hover, while the press-origin / tap queries stay live (focus gates HOVER
        /// only, never the click-through invariant). Read it to drop other hover affordances while unfocused.
        /// </summary>
        public bool WindowFocused => _focused;

        /// <summary>Derive the pointer from this frame's input. Call once per frame before hit-testing.</summary>
        public void Update(InputState input) => Update(input, null);

        /// <summary>
        /// Derive the pointer from this frame's input, mapping the cursor into design space through
        /// <paramref name="viewport"/> so all bounds helpers hit-test in design coordinates (matching draws
        /// made via <c>SpriteBatch.Begin(IDesignViewport)</c>). Pass null for raw window-pixel coordinates.
        /// The in-window guard still uses the raw window position, and gates only the frame a button first
        /// latches down: once latched, it stays down for as long as it is physically held, even after the
        /// cursor strays outside the client area (an OS-capture drag, e.g. a slider or a pan dragged past
        /// the window edge). A press that begins outside the client area is ignored unless the cursor
        /// re-enters the client area while the button is still held, at which point it latches there
        /// (unchanged from the prior behaviour).
        /// </summary>
        public void Update(InputState input, IDesignViewport? viewport)
        {
            _blocked.Clear();
            _wasDown = _down; _wasMid = _mid; _wasRight = _right;
            _prevPos = _pos;
            _focused = input.WindowFocused;

            Vector2 screen = input.MousePosition;
            // Only count presses while the pointer is inside the client area (raw window space).
            bool inWindow = input.Width <= 0
                || (screen.X >= 0 && screen.Y >= 0 && screen.X < input.Width && screen.Y < input.Height);

            _pos = viewport != null ? viewport.ScreenToDesign(screen) : screen;

            // Gate inWindow only on the frame a button first latches down, then sustain the latch
            // purely from the held read regardless of where the cursor has since strayed
            // (KhaozEngine#90). Without this, an OS-capture drag reads the held button as released
            // the instant the cursor crosses the client-area boundary, and re-entry re-latches as a
            // fresh press that resets pressOrigin mid-drag. Reads only IsDown, matching the original
            // contract (no MousePressed needed). _wasDown/_wasMid/_wasRight were captured from last
            // frame's latch above, before this frame's reassignment: "held && (wasLatched ||
            // inWindow)" reads as already latched, so ignore inWindow, or not latched, so this is a
            // fresh transition and inWindow must gate it.
            bool leftHeld = input.IsDown(MouseButton.Left);
            _down = leftHeld && (_wasDown || inWindow);
            bool midHeld = input.IsDown(MouseButton.Middle);
            _mid = midHeld && (_wasMid || inWindow);
            bool rightHeld = input.IsDown(MouseButton.Right);
            _right = rightHeld && (_wasRight || inWindow);

            // A tap whose press AND release both landed inside a single frame leaves the button already up by
            // the time Update runs, so the IsDown transitions above see nothing at all and the tap is invisible
            // to every press-origin consumer. It happens on any frame hitch, and routinely at the engine's own
            // background-throttle rates (15 Hz unfocused, 10 Hz minimized, via BackgroundThrottlePolicy). The
            // snapshot still carries the press edge, so complete the gesture here: report the frame as a
            // RELEASE, since a tap is a press plus a release and the release edge is what the tap queries key
            // on, with the press-origin at the cursor. The synthetic latch lasts exactly this frame, because
            // the next Update reassigns _wasDown from _down, which stayed false throughout.
            // Additive on purpose. A producer that never fills MousePressed (a replay, a synthesized headless
            // frame, a game's own test rig) reads exactly as it did before, so nothing here tightens the
            // contract Pointer places on InputState. See KhaozEngine#300.
            bool leftTapped = !_down && !_wasDown && inWindow && input.WasPressed(MouseButton.Left);
            if (leftTapped) _wasDown = true;
            bool midTapped = !_mid && !_wasMid && inWindow && input.WasPressed(MouseButton.Middle);
            if (midTapped) _wasMid = true;
            bool rightTapped = !_right && !_wasRight && inWindow && input.WasPressed(MouseButton.Right);
            if (rightTapped) _wasRight = true;

            // A fresh press starts a fresh, unconsumed gesture, and a same-frame tap is a fresh press that also
            // ended, so it starts one too. The right button latches its own origin and its own
            // consumed flag: a context menu opened by a right-click must not be blinded by a left-gesture consume,
            // and consuming the right gesture (so the menu that just opened does not immediately re-fire) must not
            // cancel an unrelated left tap in the same frame.
            if (IsJustPressed || leftTapped) { _pressOrigin = _pos; _consumed = false; }
            if (IsRightJustPressed || rightTapped) { _rightPressOrigin = _pos; _rightConsumed = false; }
        }

        /// <summary>Reserve a region for an overlay this frame; the layer beneath checks <see cref="IsBlocked"/>. Cleared each <see cref="Update(KhaozEngine.Windowing.InputState)"/>.</summary>
        public void BlockRegion(Rect region) => _blocked.Add(region);

        /// <summary>True if the point is inside any region reserved this frame via <see cref="BlockRegion"/>.</summary>
        public bool IsBlocked(Vector2 point)
        {
            foreach (var r in _blocked) if (r.Contains(point)) return true;
            return false;
        }

        /// <summary>
        /// Mark the current press/release gesture as already handled, so the tap queries
        /// (<see cref="IsTapIn"/>, <see cref="IsTapFromTo"/>) report false for the rest of this gesture. Cleared
        /// automatically on the next fresh press. Call when a gesture triggers a context change (a scene push/pop,
        /// an overlay opening) so a widget that appears mid-gesture does not act on a press that began before it
        /// existed. Leaves drag/hover/press-visual queries untouched.
        /// </summary>
        public void ConsumeGesture() => _consumed = true;

        /// <summary>True while the current gesture has been claimed via <see cref="ConsumeGesture"/> (until the next fresh press).</summary>
        public bool IsConsumed => _consumed;

        /// <summary>
        /// The right-button twin of <see cref="ConsumeGesture"/>: mark the current RIGHT press/release gesture as
        /// already handled, so <see cref="IsRightTapIn"/> reports false for the rest of it. Cleared automatically on
        /// the next fresh right press. Call it when a right-click opens a context menu, so the menu that appeared
        /// under the cursor does not act on the same release that spawned it. Tracked separately from the left
        /// latch, so neither button can silence the other.
        /// </summary>
        public void ConsumeRightGesture() => _rightConsumed = true;

        /// <summary>True while the current RIGHT gesture has been claimed via <see cref="ConsumeRightGesture"/> (until the next fresh right press).</summary>
        public bool IsRightConsumed => _rightConsumed;

        /// <summary>True on release only if the press-origin AND the release are both inside <paramref name="bounds"/> (the click-through invariant) and the gesture has not been consumed via <see cref="ConsumeGesture"/>.</summary>
        public bool IsTapIn(Rect bounds) => !_consumed && IsJustReleased && bounds.Contains(_pressOrigin) && bounds.Contains(_pos);

        /// <summary>True on release when the press began in <paramref name="pressOriginBounds"/> and the release is in <paramref name="releaseBounds"/>, and the gesture has not been consumed via <see cref="ConsumeGesture"/>.</summary>
        public bool IsTapFromTo(Rect pressOriginBounds, Rect releaseBounds) =>
            !_consumed && IsJustReleased && pressOriginBounds.Contains(_pressOrigin) && releaseBounds.Contains(_pos);

        /// <summary>
        /// The right-button twin of <see cref="IsTapIn"/>, carrying the same press-origin invariant: true on the
        /// right-button release only when the right press-origin AND the release are both inside
        /// <paramref name="bounds"/>, and the right gesture has not been consumed via
        /// <see cref="ConsumeRightGesture"/>. This is what a context menu hangs off: without it a consumer has to
        /// pair <see cref="IsRightJustReleased"/> with a raw position test, which the input contract forbids
        /// (hit-test through the bounds helpers, never raw position + button).
        /// </summary>
        public bool IsRightTapIn(Rect bounds) =>
            !_rightConsumed && IsRightJustReleased && bounds.Contains(_rightPressOrigin) && bounds.Contains(_pos);

        /// <summary>The right-button twin of <see cref="IsPressingIn"/>: true while the right button is held with
        /// both its press-origin and the cursor inside <paramref name="bounds"/> (the press-state visual for a
        /// right-clickable region).</summary>
        public bool IsRightPressingIn(Rect bounds) =>
            _right && bounds.Contains(_rightPressOrigin) && bounds.Contains(_pos);

        public bool IsPointerIn(Rect bounds) => bounds.Contains(_pos);
        /// <summary>True when the pointer hovers <paramref name="bounds"/> (over it, no button down) AND the window
        /// has focus - a background window reports no hover, so hover affordances (tooltips, UI hover SFX) stay
        /// silent while unfocused. The press-origin queries below are deliberately NOT focus-gated.</summary>
        public bool IsHoveringIn(Rect bounds) => _focused && !_down && bounds.Contains(_pos);
        public bool IsPressingIn(Rect bounds) => _down && bounds.Contains(_pressOrigin) && bounds.Contains(_pos);
        public bool IsReleasedOutside(Rect bounds) => IsJustReleased && !bounds.Contains(_pos);
        public bool IsDraggingIn(Rect bounds) => _down && bounds.Contains(_pressOrigin);
        public Vector2 GetDragDelta(Rect bounds) => IsDraggingIn(bounds) ? Delta : Vector2.Zero;

        /// <summary>
        /// The slider grab-gate: true while a drag whose press began inside <paramref name="bounds"/> is held
        /// (button down, press-origin in bounds), even after the cursor strays off. This is the press-origin
        /// invariant applied to a drag start - a press that began elsewhere never grabs - so the retained and
        /// immediate sliders share one rule instead of each rolling their own (<c>IsJustPressed + Contains</c>
        /// vs <c>IsDraggingIn</c>). Same condition as <see cref="IsDraggingIn"/>, named for the grab site.
        /// </summary>
        public bool IsDragStartIn(Rect bounds) => _down && bounds.Contains(_pressOrigin);
    }
}
