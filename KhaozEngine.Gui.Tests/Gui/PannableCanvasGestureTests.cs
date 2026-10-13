using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class PannableCanvasGestureTests
{
    static readonly Rect Viewport = new(200, 150, 400, 300);
    readonly InputManager _input = new();
    readonly PannableCanvas _canvas = new() { Viewport = Viewport, ContentBounds = new(0, 0, 2000, 2000) };

    public PannableCanvasGestureTests() => _canvas.Camera.Position = new(1000, 1000);

    [Fact]
    public void Magnify_pins_its_event_anchor_and_ignores_snap_stops()
    {
        _canvas.SnapZoomLevels = new[] { 1f, 2f };
        var anchor = new Vector2(520, 390);
        var world = _canvas.ScreenToWorld(anchor);
        Step(Frame(samples: new[] { Magnify(1.25f, anchor) }));
        Near(1.25f, _canvas.Camera.Zoom);
        Near(world, _canvas.ScreenToWorld(anchor));
    }

    [Fact]
    public void Precise_pan_maps_points_as_vectors_through_scaled_offset_viewport()
    {
        var mapping = new DesignViewport(800, 600);
        mapping.Update(1800, 1200);
        _canvas.Camera.Zoom = 2;
        Step(Frame(samples: new[] { Precise(new(12, -10), new(1100, 700)) }, scale: new(2, 3)), mapping: mapping);
        Near(new(994, 1007.5f), _canvas.Camera.Position);
        Near(2, _canvas.Camera.Zoom);
    }

    [Fact]
    public void Magnify_anchor_uses_the_same_scaled_offset_coordinates_as_pointer()
    {
        var mapping = new DesignViewport(800, 600);
        mapping.Update(1800, 1200);
        var world = _canvas.ScreenToWorld(new(500, 350));
        Step(Frame(samples: new[] { Magnify(2, new(1100, 700)) }, scale: new(2, 2)), mapping: mapping);
        Near(2, _canvas.Camera.Zoom);
        Near(world, _canvas.ScreenToWorld(new(500, 350)));
    }

    [Fact]
    public void Touch_pinch_zooms_then_pans_the_centroid_without_becoming_a_tap()
    {
        Step(Frame(touches: new[] { Touch(1, 400, 300, TouchPhase.Began), Touch(2, 500, 300, TouchPhase.Began) }));
        Step(Frame(touches: new[] { Touch(2, 570, 310), Touch(1, 370, 310) }));
        Near(2, _canvas.Camera.Zoom);
        Near(new(1015, 995), _canvas.Camera.Position);
        Near(new(470, 310), _canvas.WorldToScreen(new(1050, 1000)));
        Step(Frame());
        Assert.False(_canvas.TryGetGestureTap(out _));
    }

    [Fact]
    public void Frame_wheel_defaults_to_pan_and_does_not_apply_legacy_delta_twice()
    {
        Step(Frame(scroll: 10, samples: new[] { Wheel(10) }));
        Near(new(1000, 995), _canvas.Camera.Position);
        Near(1, _canvas.Camera.Zoom);
        _canvas.ScrollPanSpeed = 2;
        Step(Frame(scroll: 4, samples: new[] { Wheel(4) }));
        Near(new(1000, 987), _canvas.Camera.Position);
    }

    [Fact]
    public void Authoritative_empty_stream_never_replays_raw_scroll()
    {
        Step(Frame(scroll: 10));
        Near(new(1000, 1000), _canvas.Camera.Position);
    }

    [Fact]
    public void Unavailable_stream_retains_legacy_wheel_pan_fallback()
    {
        var state = new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(), new(400, 300), Vector2.Zero, 10, 1800, 1200);
        Step(state);
        Near(new(1000, 995), _canvas.Camera.Position);
    }

    [Fact]
    public void Wheel_zoom_animates_using_canvas_step_and_configured_duration()
    {
        _canvas.Wheel = CanvasWheelMode.Zoom;
        _canvas.ZoomStep = 1.44f;
        _canvas.GestureOptions.WheelZoomDuration = 0.2f;
        Step(Frame(scroll: 1, samples: new[] { Wheel(1) }), 0);
        Near(1, _canvas.Camera.Zoom);
        Step(Frame(), 0.1f);
        Near(1.2f, _canvas.Camera.Zoom);
        Step(Frame(), 0.1f);
        Near(1.44f, _canvas.Camera.Zoom);
        Near(new(1000, 1000), _canvas.Camera.Position);
    }

    [Fact]
    public void Ordered_wheel_samples_choose_each_adjacent_stop_when_activated()
    {
        _canvas.Wheel = CanvasWheelMode.Zoom;
        _canvas.SnapZoomLevels = new[] { 3f, 1f, 2f, 1.5f };
        _canvas.GestureOptions.WheelZoomDuration = 0.1f;
        Step(Frame(samples: new[] { Wheel(100), Wheel(100), Wheel(-100) }), 0.1f);
        Near(1.5f, _canvas.Camera.Zoom);
        Step(Frame(), 0.1f);
        Near(2, _canvas.Camera.Zoom);
        Step(Frame(), 0.1f);
        Near(1.5f, _canvas.Camera.Zoom);
    }

    [Fact]
    public void Snap_destination_is_clamped_before_factor_resolution()
    {
        _canvas.Wheel = CanvasWheelMode.Zoom;
        _canvas.MaxZoom = 2;
        _canvas.SnapZoomLevels = new[] { 1f, 4f };
        _canvas.GestureOptions.WheelZoomDuration = 0;
        Step(Frame(samples: new[] { Wheel(1) }));
        Near(2, _canvas.Camera.Zoom);
        Step(Frame(samples: new[] { Wheel(1) }));
        Near(2, _canvas.Camera.Zoom);
    }

    [Fact]
    public void Command_precise_zoom_is_continuous_and_does_not_pan_or_snap()
    {
        _canvas.SnapZoomLevels = new[] { 1f, 2f };
        Step(Frame(samples: new[] { Precise(new(50, 10), new(400, 300), command: true) }));
        Near(1.1051702f, _canvas.Camera.Zoom);
        Near(new(1000, 1000), _canvas.Camera.Position);
    }

    [Fact]
    public void Command_wheel_overrides_pan_policy_once()
    {
        _canvas.GestureOptions.WheelZoomDuration = 0;
        Step(Frame(scroll: 1, samples: new[] { Wheel(1, command: true) }));
        Near(1.1f, _canvas.Camera.Zoom);
        Near(new(1000, 1000), _canvas.Camera.Position);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Blocked_mouse_start_stays_rejected_after_blocker_disappears(int blocker)
    {
        _input.Update(Frame(down: true, press: true));
        if (blocker == 0) _input.BlockInputRegion(Viewport);
        if (blocker == 1) _input.Pointer.ConsumeGesture();
        _canvas.Update(_input, 0.01f, isBlocked: blocker == 2 ? _ => true : null);
        Step(Frame(position: new(460, 320), down: true));
        Step(Frame(position: new(460, 320), release: true));
        Near(new(1000, 1000), _canvas.Camera.Position);
        Assert.False(_canvas.TryGetGestureTap(out _));
    }

    [Fact]
    public void Touch_admission_checks_each_contact_before_canvas_reserves_itself()
    {
        Step(Frame(touches: new[] { Touch(1, 150, 300, TouchPhase.Began), Touch(2, 450, 300, TouchPhase.Began) }));
        Step(Frame(touches: new[] { Touch(1, 250, 300), Touch(2, 550, 300) }));
        Near(new(1000, 1000), _canvas.Camera.Position);
        Near(1, _canvas.Camera.Zoom);
        Assert.True(_input.Pointer.IsBlocked(new(400, 300)));
    }

    [Fact]
    public void Drag_is_admitted_before_self_reservation_and_release_is_not_a_gesture_tap()
    {
        Step(Frame(down: true, press: true));
        Assert.True(_input.Pointer.IsBlocked(new(400, 300)));
        Step(Frame(position: new(420, 310), down: true));
        Step(Frame(position: new(430, 320), release: true));
        Near(new(970, 980), _canvas.Camera.Position);
        Assert.False(_canvas.TryGetGestureTap(out var world));
        Assert.Equal(default, world);
        Assert.True(_canvas.TryGetTap(_input.Pointer, out _, out _));
    }

    [Fact]
    public void Accepted_tap_returns_world_position_and_clears_on_next_update()
    {
        Step(Frame(position: new(520, 390), down: true, press: true));
        Step(Frame(position: new(520, 390), release: true));
        Assert.True(_canvas.TryGetGestureTap(out var world));
        Near(new(1120, 1090), world);
        Assert.True(_canvas.GestureController.Tapped);
        Step(Frame());
        Assert.False(_canvas.TryGetGestureTap(out _));
    }

    [Fact]
    public void Pan_and_magnify_clamp_at_content_edges_and_zoom_limits()
    {
        _canvas.Padding = 50;
        _canvas.Camera.Position = new(160, 110);
        Step(Frame(samples: new[] { Precise(new(1000, 1000), new(400, 300)) }));
        Near(new(150, 100), _canvas.Camera.Position);
        _canvas.MaxZoom = 2;
        Step(Frame(samples: new[] { Magnify(10, new(400, 300)) }));
        Near(2, _canvas.Camera.Zoom);
        _canvas.MinZoom = 0.5f;
        Step(Frame(samples: new[] { Magnify(0.01f, new(400, 300)) }));
        Near(0.5f, _canvas.Camera.Zoom);
        Near(new(350, 250), _canvas.Camera.Position);
    }

    [Fact]
    public void Disabled_pan_gates_zoom_and_cancels_pending_animation()
    {
        _canvas.Wheel = CanvasWheelMode.Zoom;
        Step(Frame(samples: new[] { Wheel(1) }), 0);
        _canvas.EnablePan = false;
        Step(Frame(samples: new[] { Magnify(2, new(400, 300)), Precise(new(30, 20), new(400, 300)) }), 1);
        _canvas.EnablePan = true;
        Step(Frame(), 1);
        Near(1, _canvas.Camera.Zoom);
        Near(new(1000, 1000), _canvas.Camera.Position);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Lifecycle_cancellation_inhibits_held_drag_until_release(int reason)
    {
        Step(Frame(down: true, press: true));
        if (reason == 0) _canvas.CancelGestures();
        if (reason == 4) _canvas.Viewport = new(200, 150, 401, 300);
        Step(Frame(down: true, focused: reason != 1, captured: reason == 2), suppressed: reason == 3);
        Step(Frame(position: new(460, 320), down: true));
        Step(Frame(position: new(460, 320), release: true));
        Near(new(1000, 1000), _canvas.Camera.Position);
        Assert.False(_canvas.TryGetGestureTap(out _));
        Step(Frame(down: true, press: true));
        Step(Frame(position: new(420, 300), down: true));
        Near(new(980, 1000), _canvas.Camera.Position);
    }

    [Fact]
    public void Explicit_cancellation_discards_inertia_and_pending_zoom()
    {
        Step(Frame(down: true, press: true));
        Step(Frame(position: new(430, 300), down: true));
        Step(Frame(position: new(440, 300), release: true));
        _canvas.CancelGestures();
        Step(Frame(), 1);
        Near(new(960, 1000), _canvas.Camera.Position);
        _canvas.Wheel = CanvasWheelMode.Zoom;
        Step(Frame(samples: new[] { Wheel(1) }), 0);
        _canvas.CancelGestures();
        Step(Frame(), 1);
        Near(1, _canvas.Camera.Zoom);
    }

    [Fact]
    public void Keyboard_policy_requires_focus_and_uses_canvas_clamps()
    {
        _canvas.GestureOptions.KeyboardEnabled = true;
        Step(Frame(keys: new[] { Key.Right }), 0.25f);
        Near(new(1000, 1000), _canvas.Camera.Position);
        Step(Frame(keys: new[] { Key.Right }), 0.25f, keyboardFocus: true);
        Near(new(900, 1000), _canvas.Camera.Position);
    }

    void Step(InputState state, float dt = 0.01f, IDesignViewport? mapping = null,
        bool suppressed = false, bool keyboardFocus = false)
    {
        _input.Update(state, mapping);
        if (suppressed) _input.SuppressPointerInput();
        _canvas.Update(_input, dt, keyboardFocus);
    }

    static InputState Frame(Vector2? position = null, bool down = false, bool press = false, bool release = false,
        float scroll = 0, PointerGestureSample[]? samples = null, TouchPoint[]? touches = null,
        Vector2? scale = null, bool focused = true, bool captured = false, Key[]? keys = null) =>
        new(new PointerGestureFrame(samples ?? Array.Empty<PointerGestureSample>()), 0, Vector2.Zero,
            scale ?? Vector2.One, new HashSet<Key>(keys ?? Array.Empty<Key>()), new HashSet<Key>(), new HashSet<Key>(),
            down ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>(),
            press ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>(),
            position ?? new(400, 300), Vector2.Zero, scroll, 1800, 1200, touches: touches,
            windowFocused: focused, mouseReleased: release ? new HashSet<MouseButton> { MouseButton.Left }
                : new HashSet<MouseButton>(), pointerCaptured: captured);

    static PointerGestureSample Wheel(float delta, bool command = false) =>
        new(PointerGestureKind.Wheel, PointerGestureSource.Synthetic, PointerGestureInputPhase.None,
            new(400, 300), new(0, delta), commandDown: command);
    static PointerGestureSample Precise(Vector2 delta, Vector2 anchor, bool command = false) =>
        new(PointerGestureKind.PreciseScroll, PointerGestureSource.Synthetic, PointerGestureInputPhase.None,
            anchor, delta, commandDown: command);
    static PointerGestureSample Magnify(float factor, Vector2 anchor) =>
        new(PointerGestureKind.Magnify, PointerGestureSource.Synthetic, PointerGestureInputPhase.None,
            anchor, scaleFactor: factor);
    static TouchPoint Touch(long id, float x, float y, TouchPhase phase = TouchPhase.Moved) => new(id, new(x, y), phase);
    static void Near(float expected, float actual) => Assert.InRange(actual, expected - 0.002f, expected + 0.002f);
    static void Near(Vector2 expected, Vector2 actual) { Near(expected.X, actual.X); Near(expected.Y, actual.Y); }
}
