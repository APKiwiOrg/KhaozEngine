using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

internal sealed class PanZoomTestRig
{
    internal static readonly Rect Bounds = new(0, 0, 200, 200);
    internal readonly InputManager Input = new();
    internal readonly AffinePanZoomTarget Target = new();
    internal readonly PanZoomController Controller;

    internal PanZoomTestRig(PanZoomOptions? options = null) => Controller = new(options);

    internal void Step(InputState state, float dt = 0.01f, bool suppressed = false,
        Rect? bounds = null, IDesignViewport? mapping = null, bool focused = false,
        Func<Vector2, bool>? blocked = null)
    {
        Input.Update(state, mapping);
        if (suppressed) Input.SuppressPointerInput();
        Controller.Update(Input, Target, bounds ?? Bounds, dt, focused, blocked);
    }

    internal static InputState Frame(Vector2? position = null, bool down = false, bool press = false,
        bool release = false, float scroll = 0, PointerGestureSample[]? samples = null,
        TouchPoint[]? touches = null, bool windowFocused = true, bool captured = false,
        Vector2? scale = null, int width = 200, Key[]? keys = null)
    {
        var held = down ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>();
        var pressed = press ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>();
        var released = release ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>();
        var keySet = new HashSet<Key>(keys ?? Array.Empty<Key>());
        if (samples == null && scale == null)
            return new(keySet, new HashSet<Key>(), new HashSet<Key>(), held, pressed,
                position ?? new(20, 20), Vector2.Zero, scroll, width, 200, touches: touches,
                windowFocused: windowFocused, mouseReleased: released, pointerCaptured: captured);
        return new(new PointerGestureFrame(samples ?? Array.Empty<PointerGestureSample>()), 0,
            Vector2.Zero, scale ?? Vector2.One, keySet, new HashSet<Key>(), new HashSet<Key>(), held,
            pressed, position ?? new(20, 20), Vector2.Zero, scroll, width, 200, touches: touches,
            windowFocused: windowFocused, mouseReleased: released, pointerCaptured: captured);
    }

    internal static PointerGestureSample Wheel(float units, Vector2 anchor, bool command = false) =>
        new(PointerGestureKind.Wheel, PointerGestureSource.Synthetic, PointerGestureInputPhase.None,
            anchor, new Vector2(0, units), commandDown: command);

    internal static PointerGestureSample Precise(Vector2 delta, Vector2 anchor,
        PointerGestureInputPhase phase = PointerGestureInputPhase.None, bool momentum = false,
        bool command = false) => new(PointerGestureKind.PreciseScroll, PointerGestureSource.Synthetic,
            phase, anchor, delta, commandDown: command, isMomentum: momentum);

    internal static PointerGestureSample Magnify(float factor, Vector2 anchor,
        PointerGestureInputPhase phase = PointerGestureInputPhase.None) =>
        new(PointerGestureKind.Magnify, PointerGestureSource.Synthetic, phase, anchor, scaleFactor: factor);

    internal static TouchPoint Touch(long id, float x, float y, TouchPhase phase = TouchPhase.Moved) =>
        new(id, new(x, y), phase);

    internal static TouchPoint StartTouch(long id, float x, float y) => Touch(id, x, y, TouchPhase.Began);

    internal static void Near(float expected, float actual, float tolerance = 1e-4f) =>
        Assert.InRange(actual, expected - tolerance, expected + tolerance);

    internal static void Near(Vector2 expected, Vector2 actual, float tolerance = 1e-4f)
    {
        Near(expected.X, actual.X, tolerance);
        Near(expected.Y, actual.Y, tolerance);
    }
}

internal sealed class AffinePanZoomTarget : IPanZoomTarget
{
    internal Vector2 Translation;
    internal float Scale = 1;
    internal readonly List<(string Kind, Vector2 Value, float Factor)> Operations = new();
    internal Vector2 Screen(Vector2 world) => world * Scale + Translation;

    public void PanByScreenDelta(Vector2 delta)
    {
        Translation += delta;
        Operations.Add(("pan", delta, 1));
    }

    public void ZoomByFactor(float factor, Vector2 anchor)
    {
        Translation = anchor + (Translation - anchor) * factor;
        Scale *= factor;
        Operations.Add(("zoom", anchor, factor));
    }
}
