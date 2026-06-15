using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// Bounds-aware pointer over the mouse, with the press-origin click-through invariant. Feed it the
    /// frame's <see cref="InputState"/> via <see cref="Update"/>; then hit-test with the bounds helpers
    /// (<see cref="IsTapIn"/>, <see cref="IsPressingIn"/>, ...) rather than raw position + button checks.
    /// Ported from the MonoGame <c>InputManager</c> core (desktop/mouse; touch/gamepad/gestures are follow-ups).
    /// </summary>
    public sealed class Pointer
    {
        readonly List<Rect> _blocked = new();
        bool _down, _wasDown, _mid, _wasMid, _right, _wasRight;
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
        public bool IsMiddleJustReleased => !_mid && _wasMid;
        public bool IsRightDown => _right;
        public bool IsRightJustReleased => !_right && _wasRight;

        /// <summary>Derive the pointer from this frame's input. Call once per frame before hit-testing.</summary>
        public void Update(InputState input)
        {
            _blocked.Clear();
            _wasDown = _down; _wasMid = _mid; _wasRight = _right;
            _prevPos = _pos;
            _pos = input.MousePosition;

            // Only count presses while the pointer is inside the client area.
            bool inWindow = input.Width <= 0
                || (_pos.X >= 0 && _pos.Y >= 0 && _pos.X < input.Width && _pos.Y < input.Height);
            _down = input.IsDown(MouseButton.Left) && inWindow;
            _mid = input.IsDown(MouseButton.Middle) && inWindow;
            _right = input.IsDown(MouseButton.Right) && inWindow;

            if (IsJustPressed) _pressOrigin = _pos;
        }

        /// <summary>Reserve a region for an overlay this frame; the layer beneath checks <see cref="IsBlocked"/>. Cleared each <see cref="Update"/>.</summary>
        public void BlockRegion(Rect region) => _blocked.Add(region);

        /// <summary>True if the point is inside any region reserved this frame via <see cref="BlockRegion"/>.</summary>
        public bool IsBlocked(Vector2 point)
        {
            foreach (var r in _blocked) if (r.Contains(point)) return true;
            return false;
        }

        /// <summary>True on release only if the press-origin AND the release are both inside <paramref name="bounds"/> (the click-through invariant).</summary>
        public bool IsTapIn(Rect bounds) => IsJustReleased && bounds.Contains(_pressOrigin) && bounds.Contains(_pos);

        /// <summary>True on release when the press began in <paramref name="pressOriginBounds"/> and the release is in <paramref name="releaseBounds"/>.</summary>
        public bool IsTapFromTo(Rect pressOriginBounds, Rect releaseBounds) =>
            IsJustReleased && pressOriginBounds.Contains(_pressOrigin) && releaseBounds.Contains(_pos);

        public bool IsPointerIn(Rect bounds) => bounds.Contains(_pos);
        public bool IsHoveringIn(Rect bounds) => !_down && bounds.Contains(_pos);
        public bool IsPressingIn(Rect bounds) => _down && bounds.Contains(_pressOrigin) && bounds.Contains(_pos);
        public bool IsReleasedOutside(Rect bounds) => IsJustReleased && !bounds.Contains(_pos);
        public bool IsDraggingIn(Rect bounds) => _down && bounds.Contains(_pressOrigin);
        public Vector2 GetDragDelta(Rect bounds) => IsDraggingIn(bounds) ? Delta : Vector2.Zero;
    }
}
