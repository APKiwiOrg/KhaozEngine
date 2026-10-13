using System.Collections.Generic;

namespace KhaozEngine.Windowing;

/// <summary>Single-threaded pointer sample collection with independent immutable publication.</summary>
public sealed class PointerGestureAccumulator
{
    readonly List<PointerGestureSample> _pending = new();
    float _pinchScaleFactor = 1f;

    /// <summary>Validate and record an event. Rejection leaves previously collected samples intact.</summary>
    public void Add(PointerGestureSample sample)
    {
        sample.Validate();
        float next = PointerGestureFrame.ComposeScale(_pinchScaleFactor, sample);
        _pending.Add(sample);
        _pinchScaleFactor = next;
    }

    /// <summary>Freeze this frame and clear pending samples. Empty input reuses the shared empty frame.</summary>
    public PointerGestureFrame Snapshot()
    {
        if (_pending.Count == 0) return PointerGestureFrame.Empty;
        var frame = new PointerGestureFrame(_pending);
        Clear();
        return frame;
    }

    /// <summary>Discard unpublished events at an input ownership transition.</summary>
    public void Clear()
    {
        _pending.Clear();
        _pinchScaleFactor = 1f;
    }
}
