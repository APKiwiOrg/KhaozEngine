using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class PointerGestureInputTests
{
    static PointerGestureSample Scroll(PointerGestureKind kind = PointerGestureKind.Wheel) =>
        new(kind, PointerGestureSource.Synthetic, PointerGestureInputPhase.Changed,
            new Vector2(240, 360), new Vector2(2, -3), commandDown: true, isMomentum: true);

    static PointerGestureSample Magnify(float factor = 1.1f) =>
        new(PointerGestureKind.Magnify, PointerGestureSource.MacOSNative,
            PointerGestureInputPhase.Changed, new Vector2(300, 450), scaleFactor: factor);

    static InputState State(PointerGestureFrame gestures, float horizontal = 0, float vertical = 0) =>
        new(gestures, horizontal, new Vector2(3, 4), new Vector2(2, 3),
            new HashSet<Key> { Key.A }, new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            new Vector2(240, 360), new Vector2(6, 12), vertical, 1200, 900,
            touches: new[] { new TouchPoint(7, new Vector2(240, 360), TouchPhase.Moved) },
            textInput: "x", textInputAvailable: true);

    [Fact]
    public void Frames_copy_samples_and_do_not_expose_mutable_storage()
    {
        var samples = new[] { Scroll(), Magnify() };
        var frame = new PointerGestureFrame(samples);
        samples[0] = Magnify(1.2f);
        Assert.Equal(PointerGestureKind.Wheel, frame.Samples[0].Kind);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<PointerGestureSample>)frame.Samples)[0] = Magnify());
    }

    [Fact]
    public void Accumulation_keeps_event_order_and_frozen_frames()
    {
        var accumulator = new InputAccumulator();
        accumulator.OnPointerGesture(Scroll());
        accumulator.OnPointerGesture(Magnify());
        var first = accumulator.Snapshot(Vector2.Zero, true, 800, 600);
        accumulator.OnPointerGesture(Scroll(PointerGestureKind.PreciseScroll));
        var second = accumulator.Snapshot(Vector2.Zero, true, 800, 600);
        Assert.Equal(new[] { Scroll(), Magnify() }, first.Gestures.Samples);
        Assert.Single(second.Gestures.Samples);
        Assert.True(second.HasPreciseScroll);
        Assert.Same(PointerGestureFrame.Empty,
            accumulator.Snapshot(Vector2.Zero, true, 800, 600).Gestures);
    }

    [Fact]
    public void Legacy_scroll_totals_are_independent_of_authoritative_samples()
    {
        var accumulator = new InputAccumulator();
        accumulator.OnPointerGesture(Scroll(PointerGestureKind.PreciseScroll));
        accumulator.OnScroll(new Vector2(0.2f, -0.3f));
        accumulator.OnScroll(1f);
        var frame = accumulator.Snapshot(Vector2.Zero, true, 800, 600);
        Assert.Equal(0.2f, frame.HorizontalScrollDelta);
        Assert.Equal(0.7f, frame.ScrollDelta, 5);
        Assert.Single(frame.Gestures.Samples);
        Assert.Equal(new Vector2(2, -3), frame.Gestures.Samples[0].ScrollDelta);
        Assert.Equal(0f, accumulator.Snapshot(Vector2.Zero, true, 800, 600).HorizontalScrollDelta);
    }

    [Fact]
    public void Summary_multiplies_only_magnify_and_retains_zero_delta_phases()
    {
        var ended = new PointerGestureSample(PointerGestureKind.PreciseScroll,
            PointerGestureSource.MacOSNative, PointerGestureInputPhase.Ended, Vector2.Zero);
        var frame = State(new PointerGestureFrame(new[] { Scroll(), Magnify(), Magnify(1.2f), ended }));
        Assert.Equal(1f, InputState.Empty.PinchScaleFactor);
        Assert.Same(PointerGestureFrame.Empty, InputState.Empty.Gestures);
        Assert.Equal(1.32f, frame.PinchScaleFactor, 5);
        Assert.True(frame.HasPreciseScroll);
        Assert.Equal(1f, frame.Gestures.Samples[0].ScaleFactor);
        Assert.Equal(Vector2.Zero, frame.Gestures.Samples[1].ScrollDelta);
        Assert.Equal(4, frame.Gestures.Samples.Count);
    }

    [Fact]
    public void Fractional_modifier_wheel_is_not_inferred_to_be_native_precision_or_pinch()
    {
        var wheel = new PointerGestureSample(PointerGestureKind.Wheel,
            PointerGestureSource.ModifierWheelCompatibility, PointerGestureInputPhase.None,
            Vector2.Zero, new Vector2(0.1f, 0.2f), scaleFactor: 99, commandDown: true);
        var magnify = new PointerGestureSample(PointerGestureKind.Magnify,
            PointerGestureSource.Synthetic, PointerGestureInputPhase.Changed,
            Vector2.Zero, new Vector2(7, 8), scaleFactor: 1.2f);
        var frame = State(new PointerGestureFrame(new[] { wheel }));
        Assert.False(frame.HasPreciseScroll);
        Assert.Equal(1f, frame.PinchScaleFactor);
        Assert.Equal(1f, wheel.ScaleFactor);
        Assert.Equal(Vector2.Zero, magnify.ScrollDelta);
    }

    [Fact]
    public void WithoutScroll_removes_both_axes_and_scroll_samples_but_keeps_magnify()
    {
        var frame = State(new PointerGestureFrame(new[] { Scroll(), Magnify(), Magnify(1.2f) }), 4, 5);
        var cleared = frame.WithoutScroll();
        Assert.Equal(0f, cleared.ScrollDelta);
        Assert.Equal(0f, cleared.HorizontalScrollDelta);
        Assert.DoesNotContain(cleared.Gestures.Samples, s => s.Kind != PointerGestureKind.Magnify);
        Assert.Equal(frame.PinchScaleFactor, cleared.PinchScaleFactor);
        Assert.Same(frame.KeysDown, cleared.KeysDown);
        Assert.Same(frame.Touches, cleared.Touches);
        Assert.Equal(frame.FramebufferScale, cleared.FramebufferScale);
        Assert.Equal(frame.MouseDeltaPoints, cleared.MouseDeltaPoints);
        Assert.Equal(frame.TextInput, cleared.TextInput);
        Assert.True(cleared.TextInputAvailable);
        Assert.Same(cleared, cleared.WithoutScroll());
    }

    [Fact]
    public void WithoutScroll_removes_samples_even_when_legacy_totals_cancel_to_zero()
    {
        var frame = State(new PointerGestureFrame(new[] { Scroll(PointerGestureKind.PreciseScroll) }));
        var cleared = frame.WithoutScroll();
        Assert.Same(PointerGestureFrame.Empty, cleared.Gestures);
        Assert.False(cleared.HasPreciseScroll);
        Assert.NotSame(frame, cleared);
    }

    [Fact]
    public void WithoutPointerGestures_clears_all_gesture_delivery_but_keeps_other_input()
    {
        var frame = State(new PointerGestureFrame(new[] { Scroll(), Magnify() }), 4, 5);
        var cleared = frame.WithoutPointerGestures();
        Assert.Same(PointerGestureFrame.Empty, cleared.Gestures);
        Assert.Equal(0f, cleared.ScrollDelta);
        Assert.Equal(0f, cleared.HorizontalScrollDelta);
        Assert.Equal(1f, cleared.PinchScaleFactor);
        Assert.Same(frame.KeysDown, cleared.KeysDown);
        Assert.Same(frame.Touches, cleared.Touches);
        Assert.Equal(frame.MouseDeltaPoints, cleared.MouseDeltaPoints);
        Assert.Same(cleared, cleared.WithoutPointerGestures());
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void Frames_reject_invalid_scale(float scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PointerGestureFrame(new[] { Magnify(scale) }));
    }

    [Theory]
    [InlineData(float.MaxValue, 2f)]
    [InlineData(float.Epsilon, 0.5f)]
    public void Publication_rejects_magnify_summary_overflow_and_underflow(float first, float second)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PointerGestureFrame(new[] { Magnify(first), Magnify(second) }));
        var accumulator = new InputAccumulator();
        accumulator.OnPointerGesture(Magnify(first));
        Assert.Throws<ArgumentOutOfRangeException>(() => accumulator.OnPointerGesture(Magnify(second)));
        var frame = accumulator.Snapshot(Vector2.Zero, true, 800, 600);
        Assert.Single(frame.Gestures.Samples);
        Assert.Equal(first, frame.PinchScaleFactor);
        accumulator.OnPointerGesture(Magnify(second));
        Assert.Equal(second, accumulator.Snapshot(Vector2.Zero, true, 800, 600).PinchScaleFactor);
    }

    [Fact]
    public void Publication_rejects_default_samples_and_nonfinite_coordinates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PointerGestureFrame(new PointerGestureSample[1]));
        var invalidPosition = new PointerGestureSample(PointerGestureKind.Wheel,
            PointerGestureSource.Synthetic, PointerGestureInputPhase.None, new Vector2(float.NaN, 0));
        var invalidDelta = new PointerGestureSample(PointerGestureKind.PreciseScroll,
            PointerGestureSource.Synthetic, PointerGestureInputPhase.Changed, Vector2.Zero,
            new Vector2(0, float.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PointerGestureFrame(new[] { invalidPosition }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PointerGestureFrame(new[] { invalidDelta }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InputAccumulator().OnPointerGesture(default));
    }

    [Fact]
    public void Focus_and_capture_transitions_discard_pending_gestures()
    {
        var a = new InputAccumulator();
        a.OnPointerGesture(Magnify());
        a.OnFocusChanged(false);
        a.OnPointerGesture(Scroll());
        Assert.Empty(a.Snapshot(Vector2.Zero, true, 800, 600).Gestures.Samples);
        a.OnFocusChanged(true);
        a.OnPointerGesture(Magnify());
        Assert.Empty(a.Snapshot(Vector2.Zero, true, 800, 600, pointerCaptured: true).Gestures.Samples);
        a.OnPointerGesture(Scroll());
        Assert.Empty(a.Snapshot(Vector2.Zero, true, 800, 600, pointerCaptured: true).Gestures.Samples);
        a.OnPointerGesture(Magnify());
        Assert.Empty(a.Snapshot(Vector2.Zero, true, 800, 600).Gestures.Samples);
        a.OnPointerGesture(Magnify());
        Assert.Single(a.Snapshot(Vector2.Zero, true, 800, 600).Gestures.Samples);
    }

    [Fact]
    public void Suppression_filters_gestures_and_summaries_while_raw_state_remains_readable()
    {
        var frame = State(new PointerGestureFrame(new[] { Scroll(PointerGestureKind.PreciseScroll), Magnify() }), 4, 5);
        var manager = new InputManager();
        manager.Update(frame);
        Assert.Same(frame.Gestures, manager.Gestures);
        manager.SuppressPointerInput();
        Assert.True(manager.PointerInputSuppressed);
        Assert.Same(frame, manager.State);
        Assert.Same(PointerGestureFrame.Empty, manager.Gestures);
        Assert.Equal(0f, manager.HorizontalScrollDelta);
        Assert.Equal(0f, manager.ScrollDelta);
        Assert.False(manager.HasPreciseScroll);
        Assert.Equal(1f, manager.PinchScaleFactor);
        Assert.True(manager.IsKeyDown(Key.A));
        manager.Update(frame);
        Assert.False(manager.PointerInputSuppressed);
        Assert.Same(frame.Gestures, manager.Gestures);
    }

    [Fact]
    public void Coordinates_map_pixels_and_point_vectors_once_with_letterboxing()
    {
        var viewport = new DesignViewport(200, 200);
        viewport.Update(1200, 900);
        var pointer = new Pointer();
        pointer.Update(State(PointerGestureFrame.Empty), viewport);
        Assert.Equal(new Vector2(20, 80), pointer.Coordinates.PositionFromPixels(new Vector2(240, 360)));
        Assert.Equal(new Vector2(4, 6), pointer.Coordinates.VectorFromPoints(new Vector2(9, 9)));
        Assert.Equal(pointer.Position, pointer.Coordinates.PositionFromPixels(new Vector2(240, 360)));
        Assert.Equal(new Vector2(2, 2), pointer.Coordinates.VectorFromPixels(new Vector2(9, 9)));
    }

    [Fact]
    public void Coordinate_identity_snapshots_mutable_viewport_and_framebuffer_scale()
    {
        var viewport = new DesignViewport(200, 200);
        viewport.Update(1200, 900);
        var pointer = new Pointer();
        pointer.Update(State(PointerGestureFrame.Empty), viewport);
        var first = pointer.Coordinates;
        pointer.Update(State(PointerGestureFrame.Empty), viewport);
        Assert.True(first.HasSameMapping(pointer.Coordinates));
        viewport.Update(600, 600);
        Assert.Equal(new Vector2(20, 80), first.PositionFromPixels(new Vector2(240, 360)));
        pointer.Update(State(PointerGestureFrame.Empty), viewport);
        Assert.False(first.HasSameMapping(pointer.Coordinates));
        Assert.False(first.HasSameMapping(new PointerCoordinates(Vector2.One, viewport, 1200, 900)));
        Assert.False(first.HasSameMapping(new PointerCoordinates(new Vector2(2, 3), null, 1200, 900)));
        var sameTransform = new DesignViewport(200, 200);
        sameTransform.Update(1200, 900);
        Assert.False(first.HasSameMapping(new PointerCoordinates(new Vector2(2, 3), sameTransform, 1200, 900)));
        Assert.False(first.HasSameMapping(new PointerCoordinates(new Vector2(2, 3), viewport, 600, 600)));
    }

    [Fact]
    public void Raw_mapping_keeps_framebuffer_positions_and_scales_window_point_vectors()
    {
        var mapping = new PointerCoordinates(new Vector2(2, 3));
        Assert.Equal(new Vector2(240, 360), mapping.PositionFromPixels(new Vector2(240, 360)));
        Assert.Equal(new Vector2(18, 27), mapping.VectorFromPoints(new Vector2(9, 9)));
    }

    [Fact]
    public void Existing_constructor_clr_signatures_remain_available()
    {
        Type[] original = {
            typeof(IReadOnlySet<Key>), typeof(IReadOnlySet<Key>), typeof(IReadOnlySet<Key>),
            typeof(IReadOnlySet<MouseButton>), typeof(IReadOnlySet<MouseButton>),
            typeof(Vector2), typeof(Vector2), typeof(float), typeof(int), typeof(int),
            typeof(IReadOnlyList<GamepadState>), typeof(IReadOnlyList<TouchPoint>), typeof(bool),
            typeof(IReadOnlySet<Key>), typeof(IReadOnlySet<MouseButton>), typeof(string), typeof(bool), typeof(bool)
        };
        Assert.NotNull(typeof(InputState).GetConstructor(original));
        Assert.NotNull(typeof(InputState).GetConstructor(new[] { typeof(Vector2), typeof(Vector2) }.Concat(original).ToArray()));
        Assert.Equal(1f, new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(), Vector2.Zero, Vector2.Zero, 2, 800, 600).PinchScaleFactor);
    }
}
