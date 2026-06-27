using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// Bounds-aware pointer over the mouse, with the press-origin click-through invariant. Feed it the
    /// frame's <see cref="InputState"/> via <see cref="Update(KhaozEngine.Windowing.InputState)"/>; then hit-test with the bounds helpers
    /// (<see cref="IsTapIn"/>, <see cref="IsPressingIn"/>, ...) rather than raw position + button checks.
    /// Ported from the MonoGame <c>InputManager</c> core (desktop/mouse; touch/gamepad are follow-ups; pinch/swipe
    /// gestures live in <c>PinchRecognizer</c>/<c>GestureRecognizer</c>).
    /// </summary>
    public sealed class Pointer
    {
        readonly List<Rect> _blocked = new();
        bool _down, _wasDown, _mid, _wasMid, _right, _wasRight;
        bool _consumed;   // current gesture claimed by a consumer; reset on the next fresh press
        bool _focused = true;   // OS window focus, from InputState.WindowFocused; windows start focused
        Vector2 _pos, _prevPos, _pressOrigin;

        /// <summary>Current pointer position (pixels).</summary>
        public Vector2 Position => _pos;
        /// <summary>Where the current press began (valid while down and on the release frame).</summary>
        public Vector2 PressOrigin => _pressOrigin;
        /// <summary>Movement since the previous frame.</summary>
        public Vector2 Delta => _pos - _prevPos;

        public bool IsDown => _down;
        public bool IsJustPressed => _down && !_wasDown;
        public bool IsJustReleased => !_down && _wasDown;
        public bool IsMiddleDown => _mid;
        public bool IsMiddleJustPressed => _mid && !_wasMid;
        public bool IsMiddleJustReleased => !_mid && _wasMid;
        public bool IsRightDown => _right;
        public bool IsRightJustPressed => _right && !_wasRight;
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
        /// The in-window guard still uses the raw window position.
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
            _down = input.IsDown(MouseButton.Left) && inWindow;
            _mid = input.IsDown(MouseButton.Middle) && inWindow;
            _right = input.IsDown(MouseButton.Right) && inWindow;

            // A fresh press starts a fresh, unconsumed gesture.
            if (IsJustPressed) { _pressOrigin = _pos; _consumed = false; }
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

        /// <summary>True on release only if the press-origin AND the release are both inside <paramref name="bounds"/> (the click-through invariant) and the gesture has not been consumed via <see cref="ConsumeGesture"/>.</summary>
        public bool IsTapIn(Rect bounds) => !_consumed && IsJustReleased && bounds.Contains(_pressOrigin) && bounds.Contains(_pos);

        /// <summary>True on release when the press began in <paramref name="pressOriginBounds"/> and the release is in <paramref name="releaseBounds"/>, and the gesture has not been consumed via <see cref="ConsumeGesture"/>.</summary>
        public bool IsTapFromTo(Rect pressOriginBounds, Rect releaseBounds) =>
            !_consumed && IsJustReleased && pressOriginBounds.Contains(_pressOrigin) && releaseBounds.Contains(_pos);

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
