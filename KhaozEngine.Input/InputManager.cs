using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;

namespace KhaozEngine.Input;

// Centralised, game-agnostic input. Polled once per frame via Update(raw, isActive).
// Union of: unified pointer + tap invariant + region blocking (Hardpoint),
// drag/scroll/pinch (Nullwake), keyboard/gamepad/menu-navigation (SpaceGame).
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

    public InputManager(bool isMobile = false, ICoordinateTransform? transform = null)
    {
        _isMobile = isMobile;
        _transform = transform ?? IdentityTransform.Instance;
    }

    // True when the unified pointer is driven by touch rather than mouse.
    public bool IsMobile => _isMobile;

    // --- pointer ---
    public Vector2 PointerPosition => _pointerPosition;
    public Vector2 PressOrigin => _pressOrigin;
    public Vector2 PointerDelta => _pointerPosition - _previousPointerPosition;
    public bool IsPointerDown => _isPointerDown;
    public bool IsPointerJustPressed => _isPointerDown && !_wasPointerDown;
    public bool IsPointerJustReleased => !_isPointerDown && _wasPointerDown;

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

    // --- region blocking ---
    public void BlockInputRegion(Rectangle region) => _blocked.Add(region);
    public bool IsInputBlocked(Vector2 point)
    {
        for (int i = 0; i < _blocked.Count; i++)
            if (_blocked[i].Contains(point)) return true;
        return false;
    }

    // --- bounds-aware pointer helpers ---
    public bool IsTapIn(Rectangle bounds) =>
        IsPointerJustReleased && bounds.Contains(_pressOrigin) && bounds.Contains(_pointerPosition);

    public bool IsTapFromTo(Rectangle pressOriginBounds, Rectangle releaseBounds) =>
        IsPointerJustReleased && pressOriginBounds.Contains(_pressOrigin) && releaseBounds.Contains(_pointerPosition);

    public bool IsPointerIn(Rectangle bounds) => bounds.Contains(_pointerPosition);
    public bool IsHoveringIn(Rectangle bounds) => !_isPointerDown && bounds.Contains(_pointerPosition);
    public bool IsPressingIn(Rectangle bounds) =>
        _isPointerDown && bounds.Contains(_pressOrigin) && bounds.Contains(_pointerPosition);
    public bool IsReleasedOutside(Rectangle bounds) =>
        IsPointerJustReleased && !bounds.Contains(_pointerPosition);

    // --- gestures ---
    public bool IsDraggingIn(Rectangle bounds) => _isPointerDown && bounds.Contains(_pressOrigin);
    public Vector2 GetDragDelta(Rectangle bounds) => IsDraggingIn(bounds) ? PointerDelta : Vector2.Zero;

    public int ScrollWheelDelta => _isMobile ? 0 : _scrollValue - _previousScrollValue;
    public int GetScrollIn(Rectangle bounds)
    {
        if (_isMobile) return 0;
        int delta = _scrollValue - _previousScrollValue;
        return (delta != 0 && bounds.Contains(_pointerPosition)) ? delta : 0;
    }
    public bool IsMouseWheelScrolledUp => !_isMobile && (_scrollValue - _previousScrollValue) > 0;
    public bool IsMouseWheelScrolledDown => !_isMobile && (_scrollValue - _previousScrollValue) < 0;

    public bool IsPinching => _isPinching;
    public float GetPinchDeltaIn(Rectangle bounds)
    {
        if (!_isPinching || !_isMobile || _touches.Count < 2) return 0f;
        Vector2 t0 = _transform.ScreenToVirtual(_touches[0].Position);
        Vector2 t1 = _transform.ScreenToVirtual(_touches[1].Position);
        if (!bounds.Contains((t0 + t1) * 0.5f)) return 0f;
        return _pinchDistance - _previousPinchDistance;
    }

    // --- keyboard ---
    public bool IsKeyDown(Keys key) => _currentKeyboard.IsKeyDown(key);
    public bool IsKeyJustPressed(Keys key) =>
        _currentKeyboard.IsKeyDown(key) && !_previousKeyboard.IsKeyDown(key);

    public bool IsNewKeyPress(Keys key, PlayerIndex? controllingPlayer, out PlayerIndex playerIndex)
    {
        // One physical keyboard in MonoGame; player index is preserved for API compatibility.
        playerIndex = controllingPlayer ?? PlayerIndex.One;
        return _currentKeyboard.IsKeyDown(key) && _previousKeyboard.IsKeyUp(key);
    }

    // --- gamepad ---
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

    // --- menu navigation (SpaceGame semantics) ---
    public bool IsMenuSelect(PlayerIndex? p, out PlayerIndex who) =>
        IsNewKeyPress(Keys.Space, p, out who) || IsNewKeyPress(Keys.Enter, p, out who) ||
        IsNewButtonPress(Buttons.A, p, out who) || IsNewButtonPress(Buttons.Start, p, out who);

    public bool IsMenuCancel(PlayerIndex? p, out PlayerIndex who) =>
        IsNewKeyPress(Keys.Escape, p, out who) || IsNewButtonPress(Buttons.B, p, out who) ||
        IsNewButtonPress(Buttons.Back, p, out who);

    public bool IsMenuUp(PlayerIndex? p) =>
        IsNewKeyPress(Keys.Up, p, out _) || IsNewButtonPress(Buttons.DPadUp, p, out _) ||
        IsNewButtonPress(Buttons.LeftThumbstickUp, p, out _) || IsMouseWheelScrolledUp;

    public bool IsMenuDown(PlayerIndex? p) =>
        IsNewKeyPress(Keys.Down, p, out _) || IsNewButtonPress(Buttons.DPadDown, p, out _) ||
        IsNewButtonPress(Buttons.LeftThumbstickDown, p, out _) || IsMouseWheelScrolledDown;

    public bool IsSelectNext(PlayerIndex? p) =>
        IsNewKeyPress(Keys.Right, p, out _) || IsNewButtonPress(Buttons.DPadRight, p, out _);

    public bool IsSelectPrevious(PlayerIndex? p) =>
        IsNewKeyPress(Keys.Left, p, out _) || IsNewButtonPress(Buttons.DPadLeft, p, out _);

    public bool IsPauseGame(PlayerIndex? p, Rectangle? bounds = null)
    {
        bool tapped = bounds.HasValue && IsTapIn(bounds.Value);
        return IsNewKeyPress(Keys.Escape, p, out _) ||
               IsNewButtonPress(Buttons.Back, p, out _) ||
               IsNewButtonPress(Buttons.Start, p, out _) || tapped;
    }
}
