using System;
using System.Numerics;

namespace KhaozEngine.Windowing;

/// <summary>Shared drag slop, tap timing and release motion in caller coordinates.</summary>
public sealed class DragMotionTracker
{
    readonly float _slop, _tapMaxDuration, _stationaryCutoff;
    Vector2 _origin, _last, _velocityPosition, _velocity;
    double _duration, _stationary;

    public DragMotionTracker(float dragSlop = 8, float tapMaxDuration = 0.3f,
        float stationaryReleaseCutoff = 0.08f)
    {
        PanZoomOptions.ValidateNonnegative(dragSlop, nameof(dragSlop));
        PanZoomOptions.ValidateNonnegative(tapMaxDuration, nameof(tapMaxDuration));
        PanZoomOptions.ValidatePositive(stationaryReleaseCutoff, nameof(stationaryReleaseCutoff));
        _slop = dragSlop;
        _tapMaxDuration = tapMaxDuration;
        _stationaryCutoff = stationaryReleaseCutoff;
    }
    public Vector2 DragDelta { get; private set; }
    public Vector2 ReleaseVelocity { get; private set; }
    public bool TapEligible { get; private set; }
    public bool IsDragging { get; private set; }
    public bool IsActive { get; private set; }
    /// <summary>Begin at an origin. An existing drag can rebase without another slop threshold or tap.</summary>
    public void Begin(Vector2 position, bool alreadyDragging = false)
    {
        ValidatePosition(position);
        Reset();
        _origin = _last = _velocityPosition = position;
        IsActive = true;
        IsDragging = alreadyDragging;
        TapEligible = !alreadyDragging;
    }

    /// <summary>Accept all origin displacement when slop is crossed. Zero time advances position only.</summary>
    public void Move(Vector2 position, float dtSeconds)
    {
        ValidateTime(dtSeconds);
        ValidatePosition(position);
        DragDelta = Vector2.Zero;
        if (!IsActive) return;
        _duration += dtSeconds;
        TapEligible &= _duration <= _tapMaxDuration;
        if (!IsDragging && Vector2.Distance(position, _origin) > _slop)
        {
            IsDragging = true;
            TapEligible = false;
            _last = _origin;
        }
        if (IsDragging)
        {
            Vector2 delta = position - _last;
            if (IsFinite(delta)) DragDelta = delta;
            _last = position;
        }
        if (dtSeconds == 0) return;
        Vector2 movement = position - _velocityPosition;
        _velocityPosition = position;
        if (movement == Vector2.Zero) _stationary += dtSeconds;
        else
        {
            _stationary = 0;
            Vector2 velocity = movement / dtSeconds;
            _velocity = IsFinite(velocity) ? velocity : Vector2.Zero;
        }
    }

    /// <summary>Include final displacement and expose velocity unless the contact stopped before release.</summary>
    public void Release(Vector2 position, float dtSeconds)
    {
        Move(position, dtSeconds);
        ReleaseVelocity = IsActive && IsDragging && _stationary < _stationaryCutoff
            ? _velocity : Vector2.Zero;
        IsActive = IsDragging = false;
    }

    public void Reset()
    {
        DragDelta = ReleaseVelocity = _velocity = Vector2.Zero;
        _duration = _stationary = 0;
        IsActive = IsDragging = TapEligible = false;
    }

    internal static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    internal static void ValidateTime(float dtSeconds) =>
        PanZoomOptions.ValidateNonnegative(dtSeconds, nameof(dtSeconds));

    static void ValidatePosition(Vector2 position)
    {
        if (!IsFinite(position)) throw new ArgumentOutOfRangeException(nameof(position));
    }
}
