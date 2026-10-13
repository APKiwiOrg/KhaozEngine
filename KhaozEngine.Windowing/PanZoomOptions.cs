using System;

namespace KhaozEngine.Windowing;

public enum PanZoomWheelMode { Pan, Zoom }

/// <summary>Controller policy in Pointer units and real elapsed seconds.</summary>
public sealed class PanZoomOptions
{
    public float DragSlop { get; set; } = 8;
    public float TapMaxDuration { get; set; } = 0.3f;
    public float DoubleTapInterval { get; set; } = 0.3f;
    public float DoubleTapRadius { get; set; } = 24;
    public float WheelZoomFactor { get; set; } = 1.1f;
    public float WheelZoomDuration { get; set; } = 0.12f;
    public PanZoomWheelMode WheelMode { get; set; } = PanZoomWheelMode.Zoom;
    public float WheelPanSpeed { get; set; } = 40;
    /// <summary>Resolve each wheel sample once on activation, from its wheel delta and proposed factor.</summary>
    public Func<float, float, float>? WheelZoomFactorResolver { get; set; }
    public float PreciseScrollZoomLogFactorPerPoint { get; set; } = 0.01f;
    public float InertiaDecay { get; set; } = 8;
    public float StationaryReleaseCutoff { get; set; } = 0.08f;
    public bool KeyboardEnabled { get; set; }
    public float KeyboardPanSpeed { get; set; } = 400;
    public float KeyboardZoomFactorPerSecond { get; set; } = 2;

    internal void Validate()
    {
        ValidateNonnegative(DragSlop, nameof(DragSlop));
        ValidateNonnegative(TapMaxDuration, nameof(TapMaxDuration));
        ValidateNonnegative(DoubleTapInterval, nameof(DoubleTapInterval));
        ValidateNonnegative(DoubleTapRadius, nameof(DoubleTapRadius));
        ValidatePositive(WheelZoomFactor, nameof(WheelZoomFactor));
        ValidateNonnegative(WheelZoomDuration, nameof(WheelZoomDuration));
        ValidateNonnegative(WheelPanSpeed, nameof(WheelPanSpeed));
        ValidateNonnegative(PreciseScrollZoomLogFactorPerPoint, nameof(PreciseScrollZoomLogFactorPerPoint));
        ValidatePositive(InertiaDecay, nameof(InertiaDecay));
        ValidatePositive(StationaryReleaseCutoff, nameof(StationaryReleaseCutoff));
        ValidateNonnegative(KeyboardPanSpeed, nameof(KeyboardPanSpeed));
        ValidatePositive(KeyboardZoomFactorPerSecond, nameof(KeyboardZoomFactorPerSecond));
        if (!Enum.IsDefined(WheelMode)) throw new ArgumentOutOfRangeException(nameof(WheelMode));
    }

    internal static void ValidateNonnegative(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(name);
    }

    internal static void ValidatePositive(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name);
    }
}
