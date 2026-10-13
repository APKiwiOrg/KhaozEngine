namespace KhaozEngine.Windowing;

public sealed partial class InputState
{
    /// <summary>Authoritative event-time pointer samples, in callback order.</summary>
    public PointerGestureFrame Gestures { get; } = PointerGestureFrame.Empty;

    /// <summary>Legacy horizontal wheel units, independent of the ordered gesture samples.</summary>
    public float HorizontalScrollDelta { get; }

    /// <summary>Whether precise scroll occurred, including zero-delta lifecycle events.</summary>
    public bool HasPreciseScroll => Gestures.HasPreciseScroll;

    /// <summary>Product of actual magnify factors only. No magnify means identity 1.</summary>
    public float PinchScaleFactor => Gestures.PinchScaleFactor;

    /// <summary>Remove both legacy scroll axes and all pointer samples, preserving other input.</summary>
    public InputState WithoutPointerGestures() =>
        ScrollDelta == 0f && HorizontalScrollDelta == 0f && Gestures.Samples.Count == 0
            ? this : CopyPointerGestures(PointerGestureFrame.Empty, 0f, 0f);

    InputState CopyPointerGestures(PointerGestureFrame gestures, float horizontal, float vertical) =>
        new(gestures, horizontal, MouseDeltaPoints, FramebufferScale,
            KeysDown, KeysPressed, KeysReleased, MouseDown, MousePressed,
            MousePosition, MouseDelta, vertical, Width, Height, Gamepads, Touches,
            WindowFocused, KeysRepeated, MouseReleased, TextInput, TextInputAvailable, PointerCaptured);
}
