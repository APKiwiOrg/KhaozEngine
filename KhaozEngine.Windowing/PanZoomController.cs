using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Windowing;

/// <summary>Deterministic, admitted pan and anchored zoom over immutable input.</summary>
public sealed class PanZoomController
{
    readonly WheelZoomAnimation _wheel = new();
    readonly TouchPanZoomGesture _touch = new();
    readonly NativePanZoomAdmission _scrollAdmission = new(), _magnifyAdmission = new();
    DragMotionTracker _drag;
    PointerCoordinates _coordinates;
    Rect _viewport;
    Vector2 _velocity, _lastTapPosition;
    double _tapAge;
    bool _hasMapping, _mouseSequence, _mouseAdmitted, _inhibited, _mouseIgnored;
    bool _contactsHeld, _hasLastTap;

    public PanZoomController(PanZoomOptions? options = null)
    {
        Options = options ?? new();
        Options.Validate();
        _drag = CreateDrag();
    }
    public PanZoomOptions Options { get; }
    /// <summary>A single tap reports immediately. A qualifying second tap reports DoubleTapped instead.</summary>
    public bool Tapped { get; private set; }
    public Vector2 TapPosition { get; private set; }
    public bool DoubleTapped { get; private set; }
    public Vector2 DoubleTapPosition { get; private set; }
    /// <summary>Discard pending effects and tap history. Held contacts cannot restart before release.</summary>
    public void Cancel()
    {
        _inhibited |= _contactsHeld;
        _velocity = Vector2.Zero;
        _wheel.Clear();
        _drag.Reset();
        _touch.Reset();
        _scrollAdmission.Reset();
        _magnifyAdmission.Reset();
        _mouseSequence = _mouseAdmitted = _hasLastTap = false;
        Tapped = DoubleTapped = false;
    }

    public void Update(InputManager input, IPanZoomTarget target, Rect viewport, float dtSeconds,
        bool hasKeyboardFocus = false, Func<Vector2, bool>? isBlocked = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        Update(input.Pointer, input.State, target, viewport, dtSeconds, input.PointerInputSuppressed,
            hasKeyboardFocus, isBlocked);
    }

    public void Update(Pointer pointer, InputState input, IPanZoomTarget target, Rect viewport,
        float dtSeconds, bool inputSuppressed, bool hasKeyboardFocus = false,
        Func<Vector2, bool>? isBlocked = null)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(target);
        DragMotionTracker.ValidateTime(dtSeconds);
        Options.Validate();
        ValidateViewport(viewport);
        Tapped = DoubleTapped = false;
        _tapAge += dtSeconds;
        _wheel.SynchronizePolicy(Options);

        bool held = input.IsDown(MouseButton.Left);
        if (!held) _mouseIgnored = false;
        int touchCount = ActiveTouchCount(input);
        bool contacts = held || touchCount > 0;
        bool mappingChanged = _hasMapping && (_viewport != viewport || !_coordinates.HasSameMapping(pointer.Coordinates));
        _coordinates = pointer.Coordinates;
        _viewport = viewport;
        _hasMapping = true;
        if (mappingChanged || inputSuppressed || !input.WindowFocused || input.PointerCaptured
            || !ValidMapping(_coordinates) || touchCount > 2)
        {
            Cancel();
            _inhibited |= contacts;
            _contactsHeld = contacts;
            if (input.WindowFocused && !contacts) _inhibited = false;
            if (input.WindowFocused && !input.PointerCaptured && ValidMapping(_coordinates))
                ApplyKeyboard(input, target, viewport, dtSeconds, hasKeyboardFocus);
            return;
        }
        _contactsHeld = contacts;
        if (_inhibited)
        {
            if (!contacts) _inhibited = false;
            ApplyKeyboard(input, target, viewport, dtSeconds, hasKeyboardFocus);
            return;
        }

        bool admits(Vector2 position) => DragMotionTracker.IsFinite(position) && viewport.Contains(position)
            && !pointer.IsConsumed && !pointer.IsBlocked(position) && !(isBlocked?.Invoke(position) ?? false);
        void pan(Vector2 delta) => DirectPan(target, delta);
        void zoom(float factor, Vector2 anchor) => DirectZoom(target, factor, anchor);

        bool touchFrame = touchCount > 0 || _touch.IsActive || input.Touches.Count > 0;
        bool released = false;
        if (touchFrame)
        {
            _mouseIgnored |= held;
            _mouseSequence = _mouseAdmitted = false;
            _drag.Reset();
            bool wasActive = _touch.IsActive;
            _touch.Update(input.Touches, _coordinates, Options, dtSeconds, admits, pan, zoom);
            if (_touch.Started) _velocity = Vector2.Zero;
            if (_touch.Tapped && viewport.Contains(_touch.TapPosition)) RecordTap(target, _touch.TapPosition);
            released = wasActive && !_touch.IsActive;
            if (released) _velocity = _touch.ReleaseVelocity;
        }
        else if (_mouseIgnored)
        {
            if (!held) _mouseIgnored = false;
        }
        else released = UpdateMouse(pointer, held, target, dtSeconds, admits);

        foreach (PointerGestureSample sample in input.Gestures.Samples)
            ApplySample(sample, target, admits);
        Vector2 legacyScroll = new(input.HorizontalScrollDelta, input.ScrollDelta);
        if (!input.Gestures.IsAvailable && DragMotionTracker.IsFinite(legacyScroll) && legacyScroll != Vector2.Zero)
        {
            var sample = new PointerGestureSample(PointerGestureKind.Wheel, PointerGestureSource.Synthetic,
                PointerGestureInputPhase.None, input.MousePosition, legacyScroll,
                commandDown: CommandDown(input));
            ApplySample(sample, target, admits);
        }

        ApplyKeyboard(input, target, viewport, dtSeconds, hasKeyboardFocus);
        _wheel.Advance(target, dtSeconds);
        if (!released && !_mouseSequence && !_touch.IsActive && _velocity != Vector2.Zero && dtSeconds > 0)
        {
            double decay = Math.Exp(-(double)Options.InertiaDecay * dtSeconds);
            Vector2 delta = _velocity * (float)((1 - decay) / Options.InertiaDecay);
            _velocity *= (float)decay;
            if (DragMotionTracker.IsFinite(delta)) EmitPan(target, delta);
            else _velocity = Vector2.Zero;
        }
    }

    bool UpdateMouse(Pointer pointer, bool held, IPanZoomTarget target, float dtSeconds,
        Func<Vector2, bool> admits)
    {
        bool sameFrameTap = pointer.IsPressOriginFresh && pointer.IsJustReleased;
        if (!_mouseSequence && (held || sameFrameTap))
        {
            _velocity = Vector2.Zero;
            _mouseSequence = true;
            _mouseAdmitted = pointer.IsPressOriginFresh && admits(pointer.PressOrigin);
            if (_mouseAdmitted)
            {
                _drag = CreateDrag();
                _drag.Begin(pointer.PressOrigin);
            }
            if (held) return false;
        }
        if (!_mouseSequence) return false;
        if (_mouseAdmitted && (!DragMotionTracker.IsFinite(pointer.Position) || pointer.IsConsumed))
        {
            Cancel();
            return true;
        }
        if (held)
        {
            if (_mouseAdmitted)
            {
                _drag.Move(pointer.Position, dtSeconds);
                DirectPan(target, _drag.DragDelta);
            }
            return false;
        }
        if (_mouseAdmitted)
        {
            _drag.Release(pointer.Position, sameFrameTap ? 0 : dtSeconds);
            DirectPan(target, _drag.DragDelta);
            _velocity = _drag.ReleaseVelocity;
            if (_drag.TapEligible && _viewport.Contains(pointer.Position)) RecordTap(target, pointer.Position);
        }
        _mouseSequence = _mouseAdmitted = false;
        return true;
    }

    void ApplySample(PointerGestureSample sample, IPanZoomTarget target, Func<Vector2, bool> admits)
    {
        if (sample.Phase == PointerGestureInputPhase.Cancelled)
        {
            Cancel();
            return;
        }
        Vector2 anchor = _coordinates.PositionFromPixels(sample.PositionPixels);
        if (!DragMotionTracker.IsFinite(anchor)) return;
        bool admitted = sample.Kind == PointerGestureKind.Wheel ? admits(anchor)
            : (sample.Kind == PointerGestureKind.PreciseScroll ? _scrollAdmission : _magnifyAdmission)
                .Accept(sample, admits(anchor));
        if (!admitted) return;
        _velocity = Vector2.Zero;
        switch (sample.Kind)
        {
            case PointerGestureKind.Wheel:
                if (sample.CommandDown || Options.WheelMode == PanZoomWheelMode.Zoom)
                    _wheel.Enqueue(sample.ScrollDelta.Y, anchor, Options);
                else DirectPan(target, sample.ScrollDelta * Options.WheelPanSpeed);
                break;
            case PointerGestureKind.PreciseScroll:
                _wheel.Finish(target);
                if (sample.CommandDown)
                    DirectZoom(target, (float)Math.Exp((double)sample.ScrollDelta.Y
                        * Options.PreciseScrollZoomLogFactorPerPoint), anchor);
                else DirectPan(target, _coordinates.VectorFromPoints(sample.ScrollDelta));
                break;
            case PointerGestureKind.Magnify:
                _wheel.Finish(target);
                DirectZoom(target, sample.ScaleFactor, anchor);
                break;
        }
    }

    void DirectPan(IPanZoomTarget target, Vector2 delta)
    {
        if (!DragMotionTracker.IsFinite(delta) || delta == Vector2.Zero) return;
        _velocity = Vector2.Zero;
        _wheel.Finish(target);
        EmitPan(target, delta);
    }

    void DirectZoom(IPanZoomTarget target, float factor, Vector2 anchor)
    {
        if (!float.IsFinite(factor) || factor <= 0 || factor == 1 || !DragMotionTracker.IsFinite(anchor)) return;
        _velocity = Vector2.Zero;
        _wheel.Finish(target);
        target.ZoomByFactor(factor, anchor);
    }

    void RecordTap(IPanZoomTarget target, Vector2 position)
    {
        if (_hasLastTap && _tapAge <= Options.DoubleTapInterval
            && Vector2.Distance(position, _lastTapPosition) <= Options.DoubleTapRadius)
        {
            DoubleTapped = true;
            DoubleTapPosition = position;
            _hasLastTap = false;
            DirectZoom(target, 2, position);
        }
        else
        {
            Tapped = true;
            TapPosition = _lastTapPosition = position;
            _tapAge = 0;
            _hasLastTap = true;
        }
    }

    void ApplyKeyboard(InputState input, IPanZoomTarget target, Rect viewport, float dtSeconds, bool focused)
    {
        if (!Options.KeyboardEnabled || !focused || dtSeconds == 0) return;
        Vector2 direction = new((input.IsDown(Key.Right) ? 1 : 0) - (input.IsDown(Key.Left) ? 1 : 0),
            (input.IsDown(Key.Down) ? 1 : 0) - (input.IsDown(Key.Up) ? 1 : 0));
        DirectPan(target, direction * (Options.KeyboardPanSpeed * dtSeconds));
        int zoomDirection = (input.IsDown(Key.Equals) || input.IsDown(Key.KeypadAdd) ? 1 : 0)
            - (input.IsDown(Key.Minus) || input.IsDown(Key.KeypadSubtract) ? 1 : 0);
        if (zoomDirection != 0)
            DirectZoom(target, (float)Math.Pow(Options.KeyboardZoomFactorPerSecond, dtSeconds * zoomDirection),
                new Vector2(viewport.X + viewport.Width * 0.5f, viewport.Y + viewport.Height * 0.5f));
    }

    DragMotionTracker CreateDrag() => new(Options.DragSlop, Options.TapMaxDuration, Options.StationaryReleaseCutoff);

    static void EmitPan(IPanZoomTarget target, Vector2 delta)
    {
        if (delta != Vector2.Zero) target.PanByScreenDelta(delta);
    }

    static bool CommandDown(InputState input) => input.IsDown(Key.LeftControl) || input.IsDown(Key.RightControl)
        || input.IsDown(Key.LeftSuper) || input.IsDown(Key.RightSuper);

    static int ActiveTouchCount(InputState input)
    {
        int count = 0;
        foreach (TouchPoint touch in input.Touches) if (touch.Phase != TouchPhase.Ended) count++;
        return count;
    }

    static bool ValidMapping(PointerCoordinates coordinates) => DragMotionTracker.IsFinite(coordinates.ViewportScale)
        && coordinates.ViewportScale.X > 0 && coordinates.ViewportScale.Y > 0
        && DragMotionTracker.IsFinite(coordinates.ViewportOffset);

    static void ValidateViewport(Rect viewport)
    {
        PanZoomOptions.ValidateNonnegative(viewport.Width, nameof(viewport));
        PanZoomOptions.ValidateNonnegative(viewport.Height, nameof(viewport));
        if (!float.IsFinite(viewport.X) || !float.IsFinite(viewport.Y))
            throw new ArgumentOutOfRangeException(nameof(viewport));
    }
}
