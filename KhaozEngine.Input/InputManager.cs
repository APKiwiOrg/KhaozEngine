using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;

namespace KhaozEngine.Input;

/// <summary>
/// Centralised, game-agnostic input. Poll once per frame via <see cref="Update"/> with a
/// <see cref="RawInputState"/> snapshot. Unifies mouse (desktop) and touch (mobile) into a single
/// pointer, and exposes bounds-aware helpers, drag/scroll/pinch gestures, per-frame region
/// blocking, and keyboard/gamepad menu navigation.
/// <para>
/// Always hit-test with the bounds helpers (<see cref="IsTapIn"/>, <see cref="IsPressingIn"/>, …)
/// rather than raw position + button checks: <see cref="IsTapIn"/> enforces the press-origin
/// invariant that prevents click-through.
/// </para>
/// </summary>
public sealed class InputManager
{
    private readonly bool _isMobile;
    private readonly ICoordinateTransform _transform;
    private readonly List<Rectangle> _blocked = new();

    // unified pointer
    private bool _isPointerDown;
    private bool _wasPointerDown;
    private Vector2 _pointerPosition;
    private Vector2 _previousPointerPosition;
    private Vector2 _pressOrigin;

    // scroll
    private int _scrollValue;
    private int _previousScrollValue;

    // pinch (mobile)
    private IReadOnlyList<TouchPoint> _touches = Array.Empty<TouchPoint>();
    private float _pinchDistance;
    private float _previousPinchDistance;
    private bool _isPinching;

    // keyboard
    private KeyboardState _currentKeyboard;
    private KeyboardState _previousKeyboard;

    // gamepad
    private IReadOnlyList<GamePadState> _currentPads = Array.Empty<GamePadState>();
    private IReadOnlyList<GamePadState> _previousPads = Array.Empty<GamePadState>();

    /// <summary>Creates the input manager.</summary>
    /// <param name="isMobile">True to unify the pointer from touch; false to use the mouse.</param>
    /// <param name="transform">Screen-to-virtual transform; defaults to <see cref="IdentityTransform"/>.</param>
    public InputManager(bool isMobile = false, ICoordinateTransform? transform = null)
    {
        _isMobile = isMobile;
        _transform = transform ?? IdentityTransform.Instance;
    }

    /// <summary>True when the pointer is driven by touch rather than mouse.</summary>
    public bool IsMobile => _isMobile;

    /// <summary>Current pointer position in virtual coordinates.</summary>
    public Vector2 PointerPosition => _pointerPosition;

    /// <summary>Position where the current press began (valid while down and on the release frame).</summary>
    public Vector2 PressOrigin => _pressOrigin;

    /// <summary>Pointer movement since the previous frame, in virtual coordinates.</summary>
    public Vector2 PointerDelta => _pointerPosition - _previousPointerPosition;

    /// <summary>True while the pointer is pressed.</summary>
    public bool IsPointerDown => _isPointerDown;

    /// <summary>True only on the frame the pointer transitions from up to down.</summary>
    public bool IsPointerJustPressed => _isPointerDown && !_wasPointerDown;

    /// <summary>True only on the frame the pointer transitions from down to up.</summary>
    public bool IsPointerJustReleased => !_isPointerDown && _wasPointerDown;

    /// <summary>
    /// Polls input for this frame. Call once, before screens update, with a fresh snapshot.
    /// </summary>
    /// <param name="raw">The hardware snapshot (from <see cref="IRawInput.Read"/> or a test).</param>
    /// <param name="isActive">Pass <c>Game.IsActive</c>; when false the pointer reads as up to avoid ghost taps.</param>
    public void Update(RawInputState raw, bool isActive)
    {
        _blocked.Clear();
        _wasPointerDown = _isPointerDown;
        _previousPointerPosition = _pointerPosition;
        _previousKeyboard = _currentKeyboard;
        _currentKeyboard = raw.Keyboard;
        _previousScrollValue = _scrollValue;
        _scrollValue = raw.ScrollWheelValue;
        _previousPads = _currentPads;
        _currentPads = raw.GamePads;
        _touches = raw.Touches;

        bool down;
        if (_isMobile)
        {
            if (raw.Touches.Count > 0)
            {
                TouchPoint t = raw.Touches[0];
                down = t.State == TouchLocationState.Pressed || t.State == TouchLocationState.Moved;
                _pointerPosition = Project(t.Position);
            }
            else
            {
                down = false; // keep last position
            }
            UpdatePinch();
        }
        else
        {
            bool inWindow = raw.WindowBounds.IsEmpty || raw.WindowBounds.Contains(raw.MousePosition);
            down = raw.MouseLeftDown && inWindow;
            _pointerPosition = Project(new Vector2(raw.MousePosition.X, raw.MousePosition.Y));
            _isPinching = false;
        }

        if (!isActive) down = false;
        _isPointerDown = down;
        if (IsPointerJustPressed) _pressOrigin = _pointerPosition;
    }

    private Vector2 Project(Vector2 screen)
    {
        Vector2 v = _transform.ScreenToVirtual(screen);
        if (_transform.VirtualBounds is Rectangle b)
            v = Vector2.Clamp(v, new Vector2(b.Left, b.Top), new Vector2(b.Right, b.Bottom));
        return v;
    }

    private void UpdatePinch()
    {
        if (_touches.Count >= 2)
        {
            Vector2 t0 = _transform.ScreenToVirtual(_touches[0].Position);
            Vector2 t1 = _transform.ScreenToVirtual(_touches[1].Position);
            float dist = Vector2.Distance(t0, t1);
            if (!_isPinching) { _pinchDistance = dist; _previousPinchDistance = dist; _isPinching = true; }
            else { _previousPinchDistance = _pinchDistance; _pinchDistance = dist; }
        }
        else _isPinching = false;
    }

    /// <summary>
    /// Reserves a region as "owned" by an overlay this frame. The layer beneath should check
    /// <see cref="IsInputBlocked"/> before acting. Cleared at the start of each <see cref="Update"/>.
    /// </summary>
    public void BlockInputRegion(Rectangle region) => _blocked.Add(region);

    /// <summary>Returns true if the point falls inside any region reserved this frame via <see cref="BlockInputRegion"/>.</summary>
    public bool IsInputBlocked(Vector2 point)
    {
        for (int i = 0; i < _blocked.Count; i++)
            if (_blocked[i].Contains(point)) return true;
        return false;
    }

    /// <summary>
    /// True on release only if the press-origin AND the release are both inside <paramref name="bounds"/>.
    /// This is the press-origin invariant that prevents click-through; use it for taps/clicks.
    /// </summary>
    public bool IsTapIn(Rectangle bounds) =>
        IsPointerJustReleased && bounds.Contains(_pressOrigin) && bounds.Contains(_pointerPosition);

    /// <summary>True on release when the press began in <paramref name="pressOriginBounds"/> and the release is in <paramref name="releaseBounds"/>.</summary>
    public bool IsTapFromTo(Rectangle pressOriginBounds, Rectangle releaseBounds) =>
        IsPointerJustReleased && pressOriginBounds.Contains(_pressOrigin) && releaseBounds.Contains(_pointerPosition);

    /// <summary>True if the pointer is currently inside <paramref name="bounds"/>.</summary>
    public bool IsPointerIn(Rectangle bounds) => bounds.Contains(_pointerPosition);

    /// <summary>True if the pointer is inside <paramref name="bounds"/> and not pressed (desktop hover).</summary>
    public bool IsHoveringIn(Rectangle bounds) => !_isPointerDown && bounds.Contains(_pointerPosition);

    /// <summary>True while pressed with the press-origin and current position both inside <paramref name="bounds"/> (button "pressed" visual).</summary>
    public bool IsPressingIn(Rectangle bounds) =>
        _isPointerDown && bounds.Contains(_pressOrigin) && bounds.Contains(_pointerPosition);

    /// <summary>True on the frame the pointer is released outside <paramref name="bounds"/> (dismiss-on-tap-outside).</summary>
    public bool IsReleasedOutside(Rectangle bounds) =>
        IsPointerJustReleased && !bounds.Contains(_pointerPosition);

    /// <summary>True while the pointer is down and the press began inside <paramref name="bounds"/> (scroll/drag region).</summary>
    public bool IsDraggingIn(Rectangle bounds) => _isPointerDown && bounds.Contains(_pressOrigin);

    /// <summary>The pointer delta this frame, but only if the drag began inside <paramref name="bounds"/>; otherwise zero.</summary>
    public Vector2 GetDragDelta(Rectangle bounds) => IsDraggingIn(bounds) ? PointerDelta : Vector2.Zero;

    /// <summary>Raw scroll-wheel delta this frame (0 on mobile). Prefer <see cref="GetScrollIn"/> for bounded regions.</summary>
    public int ScrollWheelDelta => _isMobile ? 0 : _scrollValue - _previousScrollValue;

    /// <summary>Scroll-wheel delta this frame, but only if the pointer is inside <paramref name="bounds"/>; otherwise 0.</summary>
    public int GetScrollIn(Rectangle bounds)
    {
        if (_isMobile) return 0;
        int delta = _scrollValue - _previousScrollValue;
        return (delta != 0 && bounds.Contains(_pointerPosition)) ? delta : 0;
    }

    /// <summary>True this frame if the scroll wheel moved up (desktop only).</summary>
    public bool IsMouseWheelScrolledUp => !_isMobile && (_scrollValue - _previousScrollValue) > 0;

    /// <summary>True this frame if the scroll wheel moved down (desktop only).</summary>
    public bool IsMouseWheelScrolledDown => !_isMobile && (_scrollValue - _previousScrollValue) < 0;

    /// <summary>True while two or more touches are active (mobile).</summary>
    public bool IsPinching => _isPinching;

    /// <summary>
    /// Change in distance between the first two touches this frame (positive = spreading), but only
    /// when the pinch midpoint is inside <paramref name="bounds"/>; otherwise 0. Mobile only.
    /// </summary>
    public float GetPinchDeltaIn(Rectangle bounds)
    {
        if (!_isPinching || !_isMobile || _touches.Count < 2) return 0f;
        Vector2 t0 = _transform.ScreenToVirtual(_touches[0].Position);
        Vector2 t1 = _transform.ScreenToVirtual(_touches[1].Position);
        if (!bounds.Contains((t0 + t1) * 0.5f)) return 0f;
        return _pinchDistance - _previousPinchDistance;
    }

    /// <summary>True while <paramref name="key"/> is held.</summary>
    public bool IsKeyDown(Keys key) => _currentKeyboard.IsKeyDown(key);

    /// <summary>True only on the frame <paramref name="key"/> transitions from up to down.</summary>
    public bool IsKeyJustPressed(Keys key) =>
        _currentKeyboard.IsKeyDown(key) && !_previousKeyboard.IsKeyDown(key);

    /// <summary>
    /// True on the frame <paramref name="key"/> is newly pressed. One physical keyboard is assumed;
    /// <paramref name="controllingPlayer"/> is preserved for API compatibility and echoed to <paramref name="playerIndex"/>.
    /// </summary>
    public bool IsNewKeyPress(Keys key, PlayerIndex? controllingPlayer, out PlayerIndex playerIndex)
    {
        playerIndex = controllingPlayer ?? PlayerIndex.One;
        return _currentKeyboard.IsKeyDown(key) && _previousKeyboard.IsKeyUp(key);
    }

    /// <summary>
    /// True on the frame <paramref name="button"/> is newly pressed on the given player's pad, or on
    /// any pad when <paramref name="controllingPlayer"/> is null. The triggering player is returned in
    /// <paramref name="playerIndex"/>.
    /// </summary>
    public bool IsNewButtonPress(Buttons button, PlayerIndex? controllingPlayer, out PlayerIndex playerIndex)
    {
        if (controllingPlayer.HasValue)
        {
            playerIndex = controllingPlayer.Value;
            int i = (int)playerIndex;
            bool downNow = i < _currentPads.Count && _currentPads[i].IsButtonDown(button);
            bool upBefore = i >= _previousPads.Count || _previousPads[i].IsButtonUp(button);
            return downNow && upBefore;
        }
        for (int p = 0; p < 4; p++)
            if (IsNewButtonPress(button, (PlayerIndex)p, out playerIndex)) return true;
        playerIndex = PlayerIndex.One;
        return false;
    }

    /// <summary>Menu "select": Space/Enter, gamepad A or Start. Pass null for any player.</summary>
    public bool IsMenuSelect(PlayerIndex? p, out PlayerIndex who) =>
        IsNewKeyPress(Keys.Space, p, out who) || IsNewKeyPress(Keys.Enter, p, out who) ||
        IsNewButtonPress(Buttons.A, p, out who) || IsNewButtonPress(Buttons.Start, p, out who);

    /// <summary>Menu "cancel": Escape, gamepad B or Back. Pass null for any player.</summary>
    public bool IsMenuCancel(PlayerIndex? p, out PlayerIndex who) =>
        IsNewKeyPress(Keys.Escape, p, out who) || IsNewButtonPress(Buttons.B, p, out who) ||
        IsNewButtonPress(Buttons.Back, p, out who);

    /// <summary>Menu "up": Up arrow, D-pad up, left-stick up, or scroll-wheel up.</summary>
    public bool IsMenuUp(PlayerIndex? p) =>
        IsNewKeyPress(Keys.Up, p, out _) || IsNewButtonPress(Buttons.DPadUp, p, out _) ||
        IsNewButtonPress(Buttons.LeftThumbstickUp, p, out _) || IsMouseWheelScrolledUp;

    /// <summary>Menu "down": Down arrow, D-pad down, left-stick down, or scroll-wheel down.</summary>
    public bool IsMenuDown(PlayerIndex? p) =>
        IsNewKeyPress(Keys.Down, p, out _) || IsNewButtonPress(Buttons.DPadDown, p, out _) ||
        IsNewButtonPress(Buttons.LeftThumbstickDown, p, out _) || IsMouseWheelScrolledDown;

    /// <summary>"Select next": Right arrow or D-pad right.</summary>
    public bool IsSelectNext(PlayerIndex? p) =>
        IsNewKeyPress(Keys.Right, p, out _) || IsNewButtonPress(Buttons.DPadRight, p, out _);

    /// <summary>"Select previous": Left arrow or D-pad left.</summary>
    public bool IsSelectPrevious(PlayerIndex? p) =>
        IsNewKeyPress(Keys.Left, p, out _) || IsNewButtonPress(Buttons.DPadLeft, p, out _);

    /// <summary>
    /// "Pause": Escape, gamepad Back or Start, or — when <paramref name="bounds"/> is given —
    /// a click-through-safe tap inside it.
    /// </summary>
    public bool IsPauseGame(PlayerIndex? p, Rectangle? bounds = null)
    {
        bool tapped = bounds.HasValue && IsTapIn(bounds.Value);
        return IsNewKeyPress(Keys.Escape, p, out _) ||
               IsNewButtonPress(Buttons.Back, p, out _) ||
               IsNewButtonPress(Buttons.Start, p, out _) || tapped;
    }
}
