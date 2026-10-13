using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Windowing;

/// <summary>The units and meaning of one ordered pointer input event.</summary>
public enum PointerGestureKind { Wheel, PreciseScroll, Magnify }

/// <summary>The lifecycle of a pointer input event, independent of mouse tap or drag recognition.</summary>
public enum PointerGestureInputPhase { None, Began, Changed, Ended, Cancelled }

/// <summary>The producer of a pointer input event.</summary>
public enum PointerGestureSource { UnknownWheel, MacOSNative, ModifierWheelCompatibility, Synthetic }

/// <summary>
/// An immutable event-time pointer sample. Anchors are framebuffer pixels. Scroll deltas are wheel units for
/// Wheel and window points for PreciseScroll. Magnify uses a dimensionless scale factor with identity 1.
/// Publication through a frame or accumulator validates the sample, including rejecting default structs.
/// </summary>
public readonly record struct PointerGestureSample
{
    public PointerGestureKind Kind { get; }
    public PointerGestureSource Source { get; }
    public PointerGestureInputPhase Phase { get; }
    public Vector2 PositionPixels { get; }
    public Vector2 ScrollDelta { get; }
    public float ScaleFactor { get; }
    public bool CommandDown { get; }
    public bool IsMomentum { get; }

    public PointerGestureSample(PointerGestureKind kind, PointerGestureSource source, PointerGestureInputPhase phase,
        Vector2 positionPixels, Vector2 scrollDelta = default, float scaleFactor = 1f,
        bool commandDown = false, bool isMomentum = false)
    {
        Kind = kind;
        Source = source;
        Phase = phase;
        PositionPixels = positionPixels;
        ScrollDelta = kind == PointerGestureKind.Magnify ? Vector2.Zero : scrollDelta;
        ScaleFactor = kind == PointerGestureKind.Magnify ? scaleFactor : 1f;
        CommandDown = commandDown;
        IsMomentum = isMomentum;
    }

    internal void Validate()
    {
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Source) || !Enum.IsDefined(Phase)
            || !float.IsFinite(PositionPixels.X) || !float.IsFinite(PositionPixels.Y)
            || !float.IsFinite(ScrollDelta.X) || !float.IsFinite(ScrollDelta.Y)
            || !float.IsFinite(ScaleFactor) || ScaleFactor <= 0f)
            throw new ArgumentOutOfRangeException(nameof(PointerGestureSample), "Invalid pointer gesture sample.");
    }
}

/// <summary>Owned, immutable, ordered samples for one frame. Empty collection uses shared storage.</summary>
public sealed class PointerGestureFrame
{
    public static readonly PointerGestureFrame Empty = new(Array.Empty<PointerGestureSample>());
    internal static readonly PointerGestureFrame Unavailable = new(false);
    /// <summary>True for an authoritative stream, including an intentionally empty frame.</summary>
    public bool IsAvailable { get; } = true;
    public IReadOnlyList<PointerGestureSample> Samples { get; }
    internal bool HasScroll { get; }
    internal bool HasPreciseScroll { get; }
    internal float PinchScaleFactor { get; } = 1f;

    PointerGestureFrame(bool isAvailable)
    {
        IsAvailable = isAvailable;
        Samples = Array.Empty<PointerGestureSample>();
    }

    public PointerGestureFrame(IEnumerable<PointerGestureSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var owned = new List<PointerGestureSample>();
        foreach (var sample in samples)
        {
            sample.Validate();
            PinchScaleFactor = ComposeScale(PinchScaleFactor, sample);
            HasScroll |= sample.Kind != PointerGestureKind.Magnify;
            HasPreciseScroll |= sample.Kind == PointerGestureKind.PreciseScroll;
            owned.Add(sample);
        }
        Samples = owned.Count == 0 ? Array.Empty<PointerGestureSample>() : owned.AsReadOnly();
    }

    internal static float ComposeScale(float current, PointerGestureSample sample)
    {
        if (sample.Kind != PointerGestureKind.Magnify) return current;
        float next = current * sample.ScaleFactor;
        if (!float.IsFinite(next) || next <= 0f)
            throw new ArgumentOutOfRangeException(nameof(sample), "Magnify product must be finite and positive.");
        return next;
    }

    internal PointerGestureFrame WithoutScroll()
    {
        if (!HasScroll) return this;
        var magnify = new List<PointerGestureSample>();
        foreach (var sample in Samples)
            if (sample.Kind == PointerGestureKind.Magnify) magnify.Add(sample);
        return magnify.Count == 0 ? Empty : new PointerGestureFrame(magnify);
    }
}
