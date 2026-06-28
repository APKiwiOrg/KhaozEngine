using System;
using System.Numerics;

namespace KhaozEngine.Windowing
{
    /// <summary>Menu/player slot (0-based One..Four). The MonoGame-free replacement for XNA's PlayerIndex.</summary>
    public enum PlayerIndex { One = 0, Two = 1, Three = 2, Four = 3 }

    /// <summary>
    /// High-level, game-agnostic input for menus and screens, on the MonoGame-free 5.x stack. Poll once
    /// per frame via <see cref="Update"/> with the frame's <see cref="InputState"/> (and optionally the
    /// design <see cref="IDesignViewport"/> so pointer hit-tests land in design space). Composes a
    /// <see cref="Pointer"/> for the press-origin click-through invariant, and adds keyboard/gamepad
    /// menu-navigation, action mapping, scroll-wheel edges, and edge-detected left-stick deflections.
    /// <para>
    /// This is the 5.x rebuild of the 4.x <c>KhaozEngine.Input.InputManager</c>: XNA <c>Keys</c>/
    /// <c>Buttons</c>/<c>PlayerIndex</c> map to <see cref="Key"/>/<see cref="GamepadButton"/>/
    /// <see cref="PlayerIndex"/>, and <c>Rectangle</c> maps to <see cref="Rect"/>. Key/button edges come
    /// from the snapshot (<see cref="InputState.KeysPressed"/> / <see cref="GamepadState.ButtonsPressed"/>);
    /// stick deflections are edge-detected here against the previous frame.
    /// </para>
    /// </summary>
    public sealed class InputManager
    {
        /// <summary>Left-stick magnitude (per axis) past which a deflection counts as a menu press.</summary>
        public const float StickThreshold = 0.5f;
        const int MaxPlayers = 4;

        readonly Pointer _pointer = new();
        InputState _input = InputState.Empty;

        // Previous/current left-stick deflection per player, for edge-detected menu up/down.
        readonly bool[] _stickUpNow = new bool[MaxPlayers];
        readonly bool[] _stickUpPrev = new bool[MaxPlayers];
        readonly bool[] _stickDownNow = new bool[MaxPlayers];
        readonly bool[] _stickDownPrev = new bool[MaxPlayers];

        /// <summary>The pointer this manager drives (for callers that want the underlying instance).</summary>
        public Pointer Pointer => _pointer;

        /// <summary>
        /// This frame's immutable input snapshot (the value last handed to <see cref="Update"/>;
        /// <see cref="InputState.Empty"/> before the first call). Exposed so a retained widget that drives this
        /// manager can feed the same frame to headless helpers that take an <see cref="InputState"/> (e.g. the
        /// text-entry editing core) without reaching for the raw window input. Read-only; the snapshot is immutable.
        /// </summary>
        public InputState State => _input;

        /// <summary>
        /// Poll input for this frame. Call once, before screens update. Pass <paramref name="viewport"/>
        /// to map the pointer into design space (so bounds helpers match draws made via the same viewport);
        /// pass null for raw window-pixel coordinates.
        /// </summary>
        public void Update(InputState input, IDesignViewport? viewport = null)
        {
            _input = input;
            _pointer.Update(input, viewport);

            for (int i = 0; i < MaxPlayers; i++)
            {
                _stickUpPrev[i] = _stickUpNow[i];
                _stickDownPrev[i] = _stickDownNow[i];
                Vector2 s = input.Gamepad(i).LeftStickDeadzoned();
                _stickUpNow[i] = s.Y > StickThreshold;     // +Y = up (stick pushed up)
                _stickDownNow[i] = s.Y < -StickThreshold;
            }
        }

        // ---- pointer (delegates to the composed Pointer) --------------------

        /// <summary>Current pointer position (design space when a viewport was passed, else window pixels).</summary>
        public Vector2 PointerPosition => _pointer.Position;
        /// <summary>Where the current press began (valid while down and on the release frame).</summary>
        public Vector2 PressOrigin => _pointer.PressOrigin;
        /// <summary>Pointer movement since the previous frame.</summary>
        public Vector2 PointerDelta => _pointer.Delta;

        public bool IsPointerDown => _pointer.IsDown;
        public bool IsPointerJustPressed => _pointer.IsJustPressed;
        public bool IsPointerJustReleased => _pointer.IsJustReleased;

        public bool IsMiddleDown => _pointer.IsMiddleDown;
        public bool IsMiddleJustPressed => _pointer.IsMiddleJustPressed;
        public bool IsMiddleJustReleased => _pointer.IsMiddleJustReleased;
        public bool IsRightDown => _pointer.IsRightDown;
        public bool IsRightJustPressed => _pointer.IsRightJustPressed;
        public bool IsRightJustReleased => _pointer.IsRightJustReleased;

        /// <summary>True on release only if the press-origin AND the release are inside <paramref name="bounds"/> (click-through-safe tap).</summary>
        public bool IsTapIn(Rect bounds) => _pointer.IsTapIn(bounds);
        /// <summary>True on release when the press began in <paramref name="pressOriginBounds"/> and the release is in <paramref name="releaseBounds"/>.</summary>
        public bool IsTapFromTo(Rect pressOriginBounds, Rect releaseBounds) => _pointer.IsTapFromTo(pressOriginBounds, releaseBounds);
        public bool IsPointerIn(Rect bounds) => _pointer.IsPointerIn(bounds);
        public bool IsHoveringIn(Rect bounds) => _pointer.IsHoveringIn(bounds);
        public bool IsPressingIn(Rect bounds) => _pointer.IsPressingIn(bounds);
        public bool IsReleasedOutside(Rect bounds) => _pointer.IsReleasedOutside(bounds);
        public bool IsDraggingIn(Rect bounds) => _pointer.IsDraggingIn(bounds);
        /// <summary>The slider grab-gate: a drag whose press began inside <paramref name="bounds"/> is held (press-origin enforced).</summary>
        public bool IsDragStartIn(Rect bounds) => _pointer.IsDragStartIn(bounds);
        public Vector2 GetDragDelta(Rect bounds) => _pointer.GetDragDelta(bounds);

        /// <summary>Reserve a region for an overlay this frame; the layer beneath checks <see cref="IsInputBlocked"/>.</summary>
        public void BlockInputRegion(Rect region) => _pointer.BlockRegion(region);
        /// <summary>True if the point falls inside any region reserved this frame via <see cref="BlockInputRegion"/>.</summary>
        public bool IsInputBlocked(Vector2 point) => _pointer.IsBlocked(point);

        // ---- scroll wheel ---------------------------------------------------

        /// <summary>Raw scroll-wheel delta this frame (positive = up).</summary>
        public float ScrollDelta => _input.ScrollDelta;
        /// <summary>Integer scroll-notch delta this frame when the pointer is over <paramref name="bounds"/>, else 0.
        /// Scopes wheel scrolling to a region (e.g. a scrollable panel) using the bounds helpers rather than a raw
        /// position check.</summary>
        public int GetScrollIn(Rect bounds) => IsPointerIn(bounds) ? (int)MathF.Round(ScrollDelta) : 0;
        /// <summary>True this frame if the scroll wheel moved up.</summary>
        public bool IsMouseWheelScrolledUp => _input.ScrollDelta > 0f;
        /// <summary>True this frame if the scroll wheel moved down.</summary>
        public bool IsMouseWheelScrolledDown => _input.ScrollDelta < 0f;

        // ---- keyboard / gamepad edges --------------------------------------

        /// <summary>True while <paramref name="key"/> is held.</summary>
        public bool IsKeyDown(Key key) => _input.IsDown(key);
        /// <summary>True only on the frame <paramref name="key"/> went down.</summary>
        public bool IsKeyJustPressed(Key key) => _input.WasPressed(key);

        /// <summary>
        /// True on the frame <paramref name="key"/> is newly pressed. One keyboard is assumed;
        /// <paramref name="controllingPlayer"/> is preserved for API compatibility and echoed to <paramref name="playerIndex"/>.
        /// </summary>
        public bool IsNewKeyPress(Key key, PlayerIndex? controllingPlayer, out PlayerIndex playerIndex)
        {
            playerIndex = controllingPlayer ?? PlayerIndex.One;
            return _input.WasPressed(key);
        }

        /// <summary>
        /// True on the frame <paramref name="button"/> is newly pressed on the given player's pad, or on
        /// any pad when <paramref name="controllingPlayer"/> is null. The triggering player is returned in
        /// <paramref name="playerIndex"/>.
        /// </summary>
        public bool IsNewButtonPress(GamepadButton button, PlayerIndex? controllingPlayer, out PlayerIndex playerIndex)
        {
            if (controllingPlayer.HasValue)
            {
                playerIndex = controllingPlayer.Value;
                return _input.Gamepad((int)playerIndex).WasPressed(button);
            }
            for (int p = 0; p < MaxPlayers; p++)
            {
                if (_input.Gamepad(p).WasPressed(button)) { playerIndex = (PlayerIndex)p; return true; }
            }
            playerIndex = PlayerIndex.One;
            return false;
        }

        // ---- menu navigation -----------------------------------------------

        /// <summary>Menu "up": Up arrow, D-pad up, left-stick up (edge), or scroll-wheel up.</summary>
        public bool IsMenuUp(PlayerIndex? p = null) =>
            IsNewKeyPress(Key.Up, p, out _) || IsNewButtonPress(GamepadButton.DpadUp, p, out _) ||
            StickNewlyUp(p) || IsMouseWheelScrolledUp;

        /// <summary>Menu "down": Down arrow, D-pad down, left-stick down (edge), or scroll-wheel down.</summary>
        public bool IsMenuDown(PlayerIndex? p = null) =>
            IsNewKeyPress(Key.Down, p, out _) || IsNewButtonPress(GamepadButton.DpadDown, p, out _) ||
            StickNewlyDown(p) || IsMouseWheelScrolledDown;

        /// <summary>Menu "select": Enter/Space, gamepad A or Start. Pass null for any player.</summary>
        public bool IsMenuSelect(PlayerIndex? p, out PlayerIndex who) =>
            IsNewKeyPress(Key.Enter, p, out who) || IsNewKeyPress(Key.Space, p, out who) ||
            IsNewButtonPress(GamepadButton.A, p, out who) || IsNewButtonPress(GamepadButton.Start, p, out who);

        /// <summary>Menu "cancel": Escape, gamepad B or Back. Pass null for any player.</summary>
        public bool IsMenuCancel(PlayerIndex? p, out PlayerIndex who) =>
            IsNewKeyPress(Key.Escape, p, out who) || IsNewButtonPress(GamepadButton.B, p, out who) ||
            IsNewButtonPress(GamepadButton.Back, p, out who);

        /// <summary>"Select next": Right arrow or D-pad right.</summary>
        public bool IsSelectNext(PlayerIndex? p = null) =>
            IsNewKeyPress(Key.Right, p, out _) || IsNewButtonPress(GamepadButton.DpadRight, p, out _);

        /// <summary>"Select previous": Left arrow or D-pad left.</summary>
        public bool IsSelectPrevious(PlayerIndex? p = null) =>
            IsNewKeyPress(Key.Left, p, out _) || IsNewButtonPress(GamepadButton.DpadLeft, p, out _);

        /// <summary>
        /// "Pause": Escape, gamepad Back or Start, or - when <paramref name="bounds"/> is given - a
        /// click-through-safe tap inside it.
        /// </summary>
        public bool IsPauseGame(PlayerIndex? p = null, Rect? bounds = null)
        {
            bool tapped = bounds.HasValue && IsTapIn(bounds.Value);
            return IsNewKeyPress(Key.Escape, p, out _) ||
                   IsNewButtonPress(GamepadButton.Back, p, out _) ||
                   IsNewButtonPress(GamepadButton.Start, p, out _) || tapped;
        }

        // ---- edge-detected left-stick deflection ---------------------------

        bool StickNewlyUp(PlayerIndex? p)
        {
            if (p.HasValue) { int i = (int)p.Value; return _stickUpNow[i] && !_stickUpPrev[i]; }
            for (int i = 0; i < MaxPlayers; i++) if (_stickUpNow[i] && !_stickUpPrev[i]) return true;
            return false;
        }

        bool StickNewlyDown(PlayerIndex? p)
        {
            if (p.HasValue) { int i = (int)p.Value; return _stickDownNow[i] && !_stickDownPrev[i]; }
            for (int i = 0; i < MaxPlayers; i++) if (_stickDownNow[i] && !_stickDownPrev[i]) return true;
            return false;
        }
    }
}
