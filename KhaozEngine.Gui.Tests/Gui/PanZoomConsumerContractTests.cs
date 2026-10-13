using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class PanZoomConsumerContractTests
{
    static readonly Rect Bounds = new(100, 100, 800, 600);
    readonly InputManager _input = new();
    readonly PanZoomController _controller = new(new() { WheelZoomDuration = 0, WheelZoomFactor = 2 });
    readonly GeographicTarget _map = new(Bounds);
    readonly DraggableBottomSheet _sheet = new(new[]
    {
        BottomSheetDetent.Fixed("hidden", 0, true), BottomSheetDetent.Fixed("peek", 200),
        BottomSheetDetent.Fixed("full", 500)
    }, "peek");

    [Fact]
    public void Wrapping_target_keeps_off_centre_magnify_anchor_across_dateline()
    {
        Step(Frame(samples: new[] { Magnify(2, new(700, 250)) }));
        Near(-176, _map.Longitude);
        Near(20, _map.Span);
        Near(new(-171, 2.5f), _map.Unproject(new(700, 250)));
        Near(new(700, 250), _map.Project(new(-171, 2.5f)));
    }

    [Fact]
    public void Ordered_wheel_pan_magnify_uses_each_event_anchor_once()
    {
        Step(Frame(scroll: 99, samples: new[]
        {
            Wheel(1, new(700, 250)), Precise(new(40, 0), new(500, 300)),
            Magnify(2, new(300, 250))
        }));
        Near(-179.5f, _map.Longitude);
        Near(10, _map.Span);
        Near(new(-171, 2.5f), _map.Unproject(new(1180, 250)));
        Assert.Equal(new[] { "zoom", "pan", "zoom" }, _map.Effects);
    }

    [Theory]
    [InlineData(100, 1, -171.25f, 2.3125f)]
    [InlineData(.01f, 360, 99, -65)]
    public void Wrapping_anchor_compensation_uses_clamped_span(float factor, float span, float longitude, float latitude)
    {
        Step(Frame(samples: new[] { Magnify(factor, new(700, 250)) }));
        Near(span, _map.Span);
        Near(longitude, _map.Longitude);
        Near(latitude, _map.Latitude);
        Near(new(-171, 2.5f), _map.Unproject(new(700, 250)));
    }

    [Fact]
    public void Geographic_pan_wraps_longitude_and_clamps_polar_centre()
    {
        Step(Frame(samples: new[] { Precise(new(-100000, 100000), new(500, 250)) }));
        Near(139, _map.Longitude);
        Near(85, _map.Latitude);
        Near(40, _map.Span);
    }

    [Fact]
    public void Touch_pair_preserves_wrapped_material_point_at_new_centroid()
    {
        Step(Frame(touches: new[] { Touch(1, 650, 250, TouchPhase.Began), Touch(2, 750, 250, TouchPhase.Began) }));
        Step(Frame(touches: new[] { Touch(2, 820, 270), Touch(1, 620, 270) }));
        Near(20, _map.Span);
        Near(new(720, 270), _map.Project(new(-171, 2.5f)));
        Near(-176.5f, _map.Longitude);
        Step(Frame());
        Assert.False(_controller.Tapped);
        Assert.Equal(new[] { "zoom", "pan" }, _map.Effects);
    }

    [Fact]
    public void Canvas_target_maps_scaled_anchors_and_clamps_real_camera()
    {
        var canvas = Canvas();
        var viewport = new DesignViewport(1000, 800);
        viewport.Update(2200, 1600);
        _input.Update(Frame(samples: new[] { Magnify(2, new(1500, 500)) }, scale: new(2, 2)), viewport);
        _sheet.Update(_input, Bounds, default, 1000, 0);
        canvas.Update(_input, 0);
        Near(new(700, 250), canvas.WorldToScreen(new(1200, 850)));
        Near(new(1100, 925), canvas.Camera.Position);
        Assert.True(_input.Pointer.IsBlocked(new(700, 250)));
        _input.Update(Frame(samples: new[] { Precise(new(10000, 10000), new(1500, 500)) }, scale: new(2, 2)), viewport);
        _sheet.Update(_input, Bounds, default, 1000, 0);
        canvas.Update(_input, 0);
        Near(new(200, 150), canvas.Camera.Position);
        _input.Update(Frame(samples: new[] { Magnify(100, new(1500, 500)) }, scale: new(2, 2)), viewport);
        _sheet.Update(_input, Bounds, default, 1000, 0);
        canvas.Update(_input, 0);
        Near(4, canvas.Camera.Zoom);
    }

    [Fact]
    public void Overlay_start_remains_rejected_after_overlay_disappears()
    {
        Step(Frame(new(500, 250), down: true, press: true), overlay: new(400, 200, 200, 100));
        Step(Frame(new(600, 250), down: true));
        Step(Frame(new(600, 250), release: true));
        Near(179, _map.Longitude);
        Assert.Empty(_map.Effects);
        Assert.False(_controller.Tapped);
        Step(Frame(new(500, 250), down: true, press: true));
        Step(Frame(new(520, 250), down: true));
        Near(178, _map.Longitude);
    }

    [Fact]
    public void Sheet_start_remains_owned_after_collapse_without_map_tap_or_fling()
    {
        Step(Frame(new(500, 510), down: true, press: true));
        Assert.True(_input.Pointer.IsConsumed);
        _sheet.SetDetent("hidden", false);
        Step(Frame(new(550, 300), down: true));
        Step(Frame(new(550, 300), release: true));
        Step(Frame(), dt: 1);
        Near(179, _map.Longitude);
        Assert.Empty(_map.Effects);
        Assert.False(_controller.Tapped);
        Step(Frame(new(500, 250), down: true, press: true));
        Step(Frame(new(520, 250), down: true));
        Near(178, _map.Longitude);
    }

    [Fact]
    public void Sheet_native_cancel_rejects_orphan_momentum_then_allows_fresh_map_begin()
    {
        Step(Frame(samples: new[] { Precise(new(0, -40), new(500, 510), PointerGestureInputPhase.Began) }));
        Near(240, _sheet.Bounds.Height);
        Assert.False(_input.Pointer.IsConsumed);
        _sheet.CancelGestures();
        _sheet.SetDetent("hidden", false);
        _controller.Cancel();
        Step(Frame(samples: new[] { Precise(new(100, 0), new(500, 250), PointerGestureInputPhase.Changed, momentum: true) }));
        Step(Frame(samples: new[] { Precise(Vector2.Zero, new(500, 250), PointerGestureInputPhase.Ended, momentum: true) }));
        Assert.Empty(_map.Effects);
        Step(Frame(samples: new[] { Precise(new(40, 0), new(500, 250), PointerGestureInputPhase.Began) }));
        Near(177, _map.Longitude);
        Step(Frame(samples: new[] { Precise(Vector2.Zero, new(500, 250), PointerGestureInputPhase.Ended) }));
        Step(Frame(), dt: 1);
        Near(177, _map.Longitude);
    }

    [Fact]
    public void Explicit_view_cancel_discards_pending_zoom_and_inertia()
    {
        _controller.Options.WheelZoomDuration = .2f;
        Step(Frame(samples: new[] { Wheel(1, new(700, 250)) }), dt: 0);
        _controller.Cancel();
        Step(Frame(), dt: 1);
        Near(40, _map.Span);
        Step(Frame(new(500, 250), down: true, press: true));
        Step(Frame(new(520, 250), down: true));
        Step(Frame(new(530, 250), release: true));
        Near(177.5f, _map.Longitude);
        _controller.Cancel();
        Step(Frame(), dt: 1);
        Near(177.5f, _map.Longitude);
        Assert.False(_controller.Tapped);
    }

    [Theory]
    [InlineData(false, 20)]
    [InlineData(true, 40)]
    public void Left_latch_and_global_suppression_have_distinct_native_effects(bool suppress, float span)
    {
        _input.Update(Frame(samples: new[] { Magnify(2, new(700, 250)) }));
        _input.Pointer.ConsumeGesture();
        if (suppress) _input.SuppressPointerInput();
        _sheet.Update(_input, Bounds, default, 1000, .01f);
        _controller.Update(_input, _map, Bounds, .01f);
        Near(span, _map.Span);
    }

    [Fact]
    public void Newly_seen_moved_touch_cannot_admit_after_sheet_contact_ends()
    {
        Step(Frame(touches: new[] { Touch(1, 500, 510, TouchPhase.Began) }));
        Step(Frame(touches: new[] { Touch(1, 500, 510, TouchPhase.Ended) }));
        _sheet.SetDetent("hidden", false);
        Step(Frame(touches: new[] { Touch(2, 500, 250) }));
        Step(Frame(touches: new[] { Touch(2, 550, 250) }));
        Step(Frame());
        Assert.Empty(_map.Effects);
        Assert.False(_controller.Tapped);
        Step(Frame(touches: new[] { Touch(3, 500, 250, TouchPhase.Began) }));
        Step(Frame(touches: new[] { Touch(3, 520, 250) }));
        Near(178, _map.Longitude);
    }

    void Step(InputState state, Rect? overlay = null, float dt = .01f)
    {
        _input.Update(state);
        if (overlay.HasValue) _input.BlockInputRegion(overlay.Value);
        _sheet.Update(_input, Bounds, default, 1000, dt);
        _controller.Update(_input, _map, Bounds, dt);
        _input.BlockInputRegion(Bounds);
    }

    static PannableCanvas Canvas()
    {
        var canvas = new PannableCanvas
        {
            Viewport = Bounds,
            ContentBounds = new(0, 0, 2000, 2000),
            MinZoom = .5f,
            MaxZoom = 4
        };
        canvas.Camera.Position = new(1000, 1000);
        return canvas;
    }

    static InputState Frame(Vector2? position = null, bool down = false, bool press = false, bool release = false,
        float scroll = 0, PointerGestureSample[]? samples = null, TouchPoint[]? touches = null, Vector2? scale = null) =>
        new(new PointerGestureFrame(samples ?? Array.Empty<PointerGestureSample>()), 0, Vector2.Zero,
            scale ?? Vector2.One, new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            down ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>(),
            press ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>(),
            position ?? new(500, 250), Vector2.Zero, scroll, 2200, 1600, touches: touches,
            mouseReleased: release ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>());

    static PointerGestureSample Magnify(float factor, Vector2 anchor) =>
        new(PointerGestureKind.Magnify, PointerGestureSource.Synthetic, PointerGestureInputPhase.None, anchor, scaleFactor: factor);
    static PointerGestureSample Wheel(float delta, Vector2 anchor) =>
        new(PointerGestureKind.Wheel, PointerGestureSource.Synthetic, PointerGestureInputPhase.None, anchor, new(0, delta));
    static PointerGestureSample Precise(Vector2 delta, Vector2 anchor,
        PointerGestureInputPhase phase = PointerGestureInputPhase.None, bool momentum = false) =>
        new(PointerGestureKind.PreciseScroll, PointerGestureSource.Synthetic, phase, anchor, delta, isMomentum: momentum);
    static TouchPoint Touch(long id, float x, float y, TouchPhase phase = TouchPhase.Moved) => new(id, new(x, y), phase);
    static void Near(float expected, float actual) => Assert.InRange(actual, expected - .002f, expected + .002f);
    static void Near(Vector2 expected, Vector2 actual) { Near(expected.X, actual.X); Near(expected.Y, actual.Y); }

    sealed class GeographicTarget(Rect bounds) : IPanZoomTarget
    {
        public float Longitude { get; private set; } = 179;
        public float Latitude { get; private set; } = -5;
        public float Span { get; private set; } = 40;
        public List<string> Effects { get; } = new();
        Vector2 Centre => new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

        public Vector2 Unproject(Vector2 screen) => new(
            Wrap(Longitude + (screen.X - Centre.X) * Span / bounds.Width),
            Latitude - (screen.Y - Centre.Y) * Span / bounds.Width);
        public Vector2 Project(Vector2 world) => Centre + new Vector2(
            Wrap(world.X - Longitude), Latitude - world.Y) * bounds.Width / Span;
        public void PanByScreenDelta(Vector2 delta)
        {
            Longitude = Wrap(Longitude - delta.X * Span / bounds.Width);
            Latitude = Math.Clamp(Latitude + delta.Y * Span / bounds.Width, -85, 85);
            Effects.Add("pan");
        }
        public void ZoomByFactor(float factor, Vector2 anchor)
        {
            var before = Unproject(anchor);
            Span = Math.Clamp(Span / factor, 1, 360);
            var after = Unproject(anchor);
            Longitude = Wrap(Longitude + Wrap(before.X - after.X));
            Latitude = Math.Clamp(Latitude + before.Y - after.Y, -85, 85);
            Effects.Add("zoom");
        }
        static float Wrap(float longitude) => ((longitude + 180) % 360 + 360) % 360 - 180;
    }
}
