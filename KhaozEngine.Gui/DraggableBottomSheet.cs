using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>A bottom-docked sheet with single-contact ownership and exact body scroll handoff.
/// Update before lower maps. Bounds and content heights use Pointer coordinates.</summary>
public sealed class DraggableBottomSheet
{
    readonly BottomSheetMotion _motion;
    readonly List<Rect> _frameRegions = new();
    Rect _safeBounds;
    PointerCoordinates _coordinates;
    InputManager? _lastInput;
    InputState? _leftReleaseState;
    bool _hasLayout, _contactsHeld, _inhibited, _ownsLeftUntilRelease, _consumeLeftUntilRelease;
    bool _mouseSequence, _touchSequence, _admitted, _handle, _mouseIgnored;
    bool _nativeSequence, _nativeAdmitted, _nativeEnded, _nativeHandle;
    long _touchId;
    Vector2 _touchPosition;
    float _contentHeight;

    public BottomSheetOptions Options => _motion.Options;
    public Rect Bounds => new(_safeBounds.X, _safeBounds.Bottom - _motion.Height, _safeBounds.Width, _motion.Height);
    public Rect HandleBounds => new(Bounds.X, Bounds.Y, Bounds.Width, MathF.Min(Options.HandleHeight, Bounds.Height));
    public Rect ContentBounds => new(Bounds.X, HandleBounds.Bottom, Bounds.Width, MathF.Max(0, Bounds.Height - HandleBounds.Height));
    public string CurrentDetentId => _motion.CurrentDetentId;
    public string TargetDetentId => _motion.TargetDetentId;
    public float ScrollOffset => _motion.ScrollOffset;
    public event Action<string>? DetentChanged;
    public event Action? Dismissed;
    /// <summary>Caller-owned optional white texture for chrome. This sheet never allocates or disposes it.</summary>
    public Texture2D? WhiteTexture { get; set; }
    public Vector4 Background { get; set; } = GuiTheme.Default.Surface;
    public Vector4 HandleColor { get; set; } = GuiTheme.Default.Border;

    public DraggableBottomSheet(IReadOnlyList<BottomSheetDetent> detents, string initialDetentId,
        BottomSheetOptions? options = null)
    {
        _motion = new(detents, initialDetentId, options);
        _motion.DetentChanged += id => DetentChanged?.Invoke(id);
        _motion.Dismissed += () => Dismissed?.Invoke();
    }

    /// <summary>Resolve body-only content height and host safe area. Layout or coordinate changes cancel
    /// held gestures and settle the same target ID. Suppressed raw State is used only for release detection.
    /// Left consumption gates mouse admission. Touch and native admission use geometry and blocked regions.
    /// Command scroll and magnify are reserved zoom intent and never move a sheet.</summary>
    public void Update(InputManager input, Rect availableBounds, SafeAreaInsets safeArea,
        float contentHeight, float dtSeconds)
    {
        ArgumentNullException.ThrowIfNull(input);
        BottomSheetOptions.Nonnegative(dtSeconds, nameof(dtSeconds));
        BottomSheetOptions.Nonnegative(contentHeight, nameof(contentHeight));
        ValidateBounds(availableBounds);
        _frameRegions.Clear();
        var state = input.State;
        var pointer = input.Pointer;
        ExpireReleasedLeft(input);
        Rect safe = safeArea.Apply(availableBounds);
        bool changed = _hasLayout && (_safeBounds != safe || _contentHeight != contentHeight
            || !_coordinates.HasSameMapping(pointer.Coordinates));
        bool held = state.IsDown(MouseButton.Left);
        if (!held) _mouseIgnored = false;
        int active = 0;
        foreach (var touch in state.Touches) if (touch.Phase != TouchPhase.Ended) active++;
        bool contacts = held || active > 0;
        _lastInput = input;
        _safeBounds = safe;
        _coordinates = pointer.Coordinates;
        _hasLayout = true;
        _contentHeight = contentHeight;
        _motion.Resolve(safe.Height, contentHeight);
        _frameRegions.Add(Bounds);
        if (changed || input.PointerInputSuppressed || !state.WindowFocused || state.PointerCaptured
            || !ValidMapping(_coordinates) || active > 1)
        {
            CancelGestures();
            if (changed) _motion.SetDetent(TargetDetentId, false);
            _inhibited |= contacts;
            _contactsHeld = contacts;
            if (state.WindowFocused && !contacts) _inhibited = false;
            Reserve(pointer);
            return;
        }
        _contactsHeld = contacts;
        if (_inhibited)
        {
            Reserve(pointer);
            if (!contacts) _inhibited = false;
            return;
        }

        bool direct = false;
        if (active > 0 || _touchSequence || state.Touches.Count > 0)
        {
            _mouseIgnored |= held;
            if (_mouseSequence)
            {
                _motion.Cancel();
                _mouseSequence = _admitted = false;
            }
            direct = UpdateTouch(state.Touches, pointer, dtSeconds);
        }
        else if (!_mouseIgnored) direct = UpdateMouse(pointer, held, dtSeconds);
        if (!_mouseSequence && !_touchSequence)
        {
            foreach (var sample in input.Gestures.Samples) direct |= ApplySample(sample, pointer);
            if (!input.Gestures.IsAvailable && state.ScrollDelta != 0 && float.IsFinite(state.ScrollDelta))
                direct |= ApplySample(new(PointerGestureKind.Wheel, PointerGestureSource.Synthetic,
                    PointerGestureInputPhase.None, state.MousePosition, new(0, state.ScrollDelta),
                    commandDown: state.IsDown(Key.LeftControl) || state.IsDown(Key.RightControl)
                        || state.IsDown(Key.LeftSuper) || state.IsDown(Key.RightSuper)), pointer);
        }
        if (!direct && !_motion.IsActive) _motion.Advance(dtSeconds);
        Reserve(pointer);
    }

    /// <summary>Cancel motion and native admission. Held contacts must release before restarting.
    /// An owned left mouse gesture remains consumed through its physical release.</summary>
    public void CancelGestures()
    {
        if (_lastInput is { } input) ExpireReleasedLeft(input);
        _inhibited |= _contactsHeld;
        _consumeLeftUntilRelease |= _ownsLeftUntilRelease;
        if (_consumeLeftUntilRelease) _lastInput?.Pointer.ConsumeGesture();
        _motion.Cancel();
        _mouseSequence = _touchSequence = _admitted = false;
        ResetNative();
    }

    /// <summary>Select a named detent. A programmatic selection cancels any in-flight contact first.</summary>
    public void SetDetent(string id, bool animated)
    {
        CancelGestures();
        _motion.SetDetent(id, animated);
    }

    /// <summary>Caller-driven handle action, including keyboard activation with caller-owned focus.</summary>
    public void ToggleHandle(bool animated = true)
    {
        CancelGestures();
        _motion.ToggleHandle(animated);
    }

    bool UpdateMouse(Pointer pointer, bool held, float dt)
    {
        bool sameFrameTap = pointer.IsPressOriginFresh && pointer.IsJustReleased;
        if (!_mouseSequence && (held || sameFrameTap))
        {
            _mouseSequence = true;
            _admitted = pointer.IsPressOriginFresh && !pointer.IsConsumed && Admits(pointer, pointer.PressOrigin);
            _handle = HandleBounds.Contains(pointer.PressOrigin);
            if (_admitted)
            {
                _motion.Begin(pointer.PressOrigin, _handle);
                OwnLeftContact(pointer, _handle);
            }
            if (held) return _admitted;
        }
        if (!_mouseSequence) return false;
        bool admitted = _admitted;
        if (_admitted && !Finite(pointer.Position))
        {
            CancelGestures();
            return true;
        }
        if (held)
        {
            if (_admitted)
            {
                _motion.Move(pointer.Position, dt);
                OwnLeftContact(pointer, _motion.IsDragging);
            }
            return _admitted;
        }
        if (_admitted)
        {
            _motion.Release(pointer.Position, sameFrameTap ? 0 : dt,
                _handle && HandleBounds.Contains(pointer.Position));
            OwnLeftContact(pointer, _handle || _motion.ReleasedDrag);
        }
        _mouseSequence = _admitted = false;
        return admitted;
    }

    bool UpdateTouch(IReadOnlyList<TouchPoint> touches, Pointer pointer, float dt)
    {
        TouchPoint? active = null, ended = null;
        var ids = new HashSet<long>();
        bool valid = true;
        foreach (var contact in touches)
        {
            Vector2 position = _coordinates.PositionFromPixels(contact.Position);
            valid &= Finite(position) && Enum.IsDefined(contact.Phase) && ids.Add(contact.Id);
            if (contact.Phase != TouchPhase.Ended) active = contact;
            else if (_touchSequence && contact.Id == _touchId) ended = contact;
        }
        if (!_touchSequence)
        {
            if (!active.HasValue) return false;
            _touchSequence = true;
            _touchId = active.Value.Id;
            _touchPosition = _coordinates.PositionFromPixels(active.Value.Position);
            _admitted = valid && active.Value.Phase == TouchPhase.Began && Admits(pointer, _touchPosition);
            _handle = HandleBounds.Contains(_touchPosition);
            if (_admitted)
            {
                _motion.Begin(_touchPosition, _handle);
                OwnLeftContact(pointer, _handle);
            }
            return _admitted;
        }
        if (!valid || (active.HasValue && (active.Value.Id != _touchId || active.Value.Phase == TouchPhase.Began)))
        {
            if (_admitted) OwnLeftContact(pointer, true);
            _motion.Cancel();
            _admitted = false;
        }
        bool admitted = _admitted;
        if (_admitted) OwnLeftContact(pointer, _handle);
        if (active.HasValue)
        {
            _touchPosition = _coordinates.PositionFromPixels(active.Value.Position);
            if (_admitted)
            {
                _motion.Move(_touchPosition, dt);
                OwnLeftContact(pointer, _motion.IsDragging);
            }
            return _admitted;
        }
        if (_admitted)
        {
            if (ended.HasValue) _touchPosition = _coordinates.PositionFromPixels(ended.Value.Position);
            _motion.Release(_touchPosition, dt, _handle && HandleBounds.Contains(_touchPosition));
            OwnLeftContact(pointer, _handle || _motion.ReleasedDrag);
        }
        _touchSequence = _admitted = false;
        return admitted;
    }

    bool ApplySample(PointerGestureSample sample, Pointer pointer)
    {
        if (sample.Kind == PointerGestureKind.Magnify) return false;
        Vector2 anchor = _coordinates.PositionFromPixels(sample.PositionPixels);
        if (sample.Kind == PointerGestureKind.Wheel)
        {
            if (sample.CommandDown || !Admits(pointer, anchor) || !ContentBounds.Contains(anchor)) return false;
            float delta = sample.ScrollDelta.Y * Options.WheelScrollSpeed;
            if (!float.IsFinite(delta)) return false;
            _motion.ScrollExpandedContent(delta);
            return true;
        }
        if (sample.Phase == PointerGestureInputPhase.Cancelled)
        {
            bool owned = _nativeSequence && _nativeAdmitted;
            ResetNative();
            if (owned) _motion.Cancel();
            return owned;
        }
        bool admitted, handle, ownsSequence;
        if (sample.IsMomentum)
        {
            ownsSequence = _nativeSequence && _nativeAdmitted;
            admitted = ownsSequence && !sample.CommandDown;
            handle = _nativeHandle;
        }
        else if (sample.Phase == PointerGestureInputPhase.None)
        {
            ownsSequence = false;
            admitted = !sample.CommandDown && Admits(pointer, anchor);
            handle = HandleBounds.Contains(anchor);
        }
        else
        {
            if (sample.Phase == PointerGestureInputPhase.Began)
            {
                _nativeSequence = true;
                _nativeEnded = false;
                _nativeAdmitted = !sample.CommandDown && Admits(pointer, anchor);
                _nativeHandle = HandleBounds.Contains(anchor);
            }
            ownsSequence = _nativeSequence && !_nativeEnded && _nativeAdmitted;
            admitted = ownsSequence && !sample.CommandDown;
            handle = _nativeHandle;
        }
        if (admitted)
        {
            _frameRegions.Add(Bounds);
            float delta = _coordinates.VectorFromPoints(sample.ScrollDelta).Y;
            if (float.IsFinite(delta))
            {
                _motion.Cancel();
                if (handle) _motion.ApplyHandleDelta(delta);
                else _motion.ApplyContentDelta(delta);
            }
        }
        bool terminal = ownsSequence && sample.Phase == PointerGestureInputPhase.Ended;
        if (terminal) _motion.Snap(0);
        if (sample.Phase == PointerGestureInputPhase.Ended)
        {
            if (sample.IsMomentum) ResetNative();
            else _nativeEnded = true;
        }
        return admitted || terminal;
    }

    void ResetNative() => _nativeSequence = _nativeAdmitted = _nativeEnded = _nativeHandle = false;
    bool Admits(Pointer pointer, Vector2 position) => Finite(position) && Bounds.Width > 0 && Bounds.Height > 0
        && Bounds.Contains(position) && !pointer.IsBlocked(position);
    void OwnLeftContact(Pointer pointer, bool consume)
    {
        _ownsLeftUntilRelease |= pointer.IsDown || pointer.IsPressOriginFresh;
        if (!_ownsLeftUntilRelease || !consume) return;
        _consumeLeftUntilRelease = true;
        pointer.ConsumeGesture();
    }
    void ExpireReleasedLeft(InputManager input)
    {
        if (_leftReleaseState == null || (ReferenceEquals(_lastInput, input)
            && ReferenceEquals(_leftReleaseState, input.State))) return;
        _leftReleaseState = null;
        _ownsLeftUntilRelease = _consumeLeftUntilRelease = false;
    }
    void Reserve(Pointer pointer)
    {
        foreach (var bounds in _frameRegions)
            if (bounds.Width > 0 && bounds.Height > 0) pointer.BlockRegion(bounds);
        if (Bounds.Width > 0 && Bounds.Height > 0) pointer.BlockRegion(Bounds);
        if (_consumeLeftUntilRelease) pointer.ConsumeGesture();
        if (_ownsLeftUntilRelease && _lastInput?.State.WindowFocused == true
            && !_lastInput.State.IsDown(MouseButton.Left))
            _leftReleaseState = _lastInput.State;
    }

    /// <summary>Draw optional caller-textured chrome and invoke the body callback with its viewport and offset.
    /// Content is clipped inside the caller's existing clip. Caller owns batch lifetime and localized labels.</summary>
    public void Draw(SpriteBatch batch, Action<Rect, float> drawContent)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(drawContent);
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        if (WhiteTexture is { } white)
        {
            GuiDraw.Fill(batch, white, Bounds, Background);
            Rect handle = HandleBounds;
            float width = MathF.Min(40, handle.Width), height = MathF.Min(4, handle.Height);
            GuiDraw.Fill(batch, white, new(handle.X + (handle.Width - width) / 2,
                handle.Y + (handle.Height - height) / 2, width, height), HandleColor);
        }
        batch.SetScissor(ContentBounds);
        try { drawContent(ContentBounds, ScrollOffset); }
        finally { batch.ClearScissor(); }
    }

    static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    static bool ValidMapping(PointerCoordinates coordinates) => Finite(coordinates.ViewportScale)
        && coordinates.ViewportScale.X > 0 && coordinates.ViewportScale.Y > 0 && Finite(coordinates.ViewportOffset);
    static void ValidateBounds(Rect bounds)
    {
        BottomSheetOptions.Nonnegative(bounds.Width, nameof(bounds));
        BottomSheetOptions.Nonnegative(bounds.Height, nameof(bounds));
        if (!float.IsFinite(bounds.X) || !float.IsFinite(bounds.Y)
            || !float.IsFinite(bounds.Right) || !float.IsFinite(bounds.Bottom)) throw new ArgumentOutOfRangeException(nameof(bounds));
    }
}
