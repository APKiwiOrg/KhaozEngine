using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class DraggableBottomSheetTests
{
    static readonly Rect Available = new(100, 100, 800, 800);
    readonly InputManager _input = new();
    readonly DraggableBottomSheet _sheet = new(new[]
    {
        BottomSheetDetent.Fixed("closed", 0, true), BottomSheetDetent.Fixed("peek", 200),
        BottomSheetDetent.Fixed("full", 600)
    }, "peek", new BottomSheetOptions { HandleToggleDetentIds = new[] { "peek", "full" } });

    public DraggableBottomSheetTests() => Step(Frame(), 0);

    [Fact]
    public void Safe_area_and_handle_content_rectangles_use_pointer_units()
    {
        Step(Frame(), safe: new(20, 30, 10, 40));
        Assert.Equal(new Rect(110, 670, 750, 200), _sheet.Bounds);
        Assert.Equal(new Rect(110, 670, 750, 32), _sheet.HandleBounds);
        Assert.Equal(new Rect(110, 702, 750, 168), _sheet.ContentBounds);
        Step(Frame(), safe: new(900, 0, 900, 0));
        Assert.Equal(0, _sheet.Bounds.Height);
        Assert.Equal(0, _sheet.Bounds.Width);
        Assert.Equal("peek", _sheet.CurrentDetentId);
    }

    [Fact]
    public void Handle_drag_only_changes_sheet_and_release_motion_does_not_tap()
    {
        Step(Frame(new(400, 710), down: true, press: true));
        Step(Frame(new(400, 300), down: true));
        Assert.Equal(600, _sheet.Bounds.Height);
        Assert.Equal(0, _sheet.ScrollOffset);
        Step(Frame(new(400, 300)));
        Step(Frame(), .18f);
        Assert.Equal("full", _sheet.CurrentDetentId);
    }

    [Fact]
    public void Content_drag_transfers_exact_residual_in_both_directions()
    {
        Step(Frame(new(400, 760), down: true, press: true));
        Step(Frame(new(400, 330), down: true));
        Assert.Equal(600, _sheet.Bounds.Height);
        Assert.Equal(30, _sheet.ScrollOffset);
        Step(Frame(new(400, 380), down: true));
        Assert.Equal(580, _sheet.Bounds.Height);
        Assert.Equal(0, _sheet.ScrollOffset);
    }

    [Fact]
    public void Horizontal_drag_never_resizes_or_scrolls()
    {
        Step(Frame(new(400, 760), down: true, press: true));
        Step(Frame(new(450, 760), down: true));
        Assert.Equal(200, _sheet.Bounds.Height);
        Assert.Equal(0, _sheet.ScrollOffset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Handle_tap_toggles_for_mouse_and_touch(bool touch)
    {
        if (touch)
        {
            Step(Frame(touches: new[] { Touch(1, 710, TouchPhase.Began) }));
            Step(Frame(touches: new[] { Touch(1, 710, TouchPhase.Ended) }));
        }
        else Step(Frame(new(400, 710), press: true));
        Assert.Equal("full", _sheet.TargetDetentId);
        Step(Frame(), .18f);
        Assert.Equal("full", _sheet.CurrentDetentId);
    }

    [Fact]
    public void Second_touch_cancels_until_every_contact_releases()
    {
        Step(Frame(touches: new[] { Touch(1, 760, TouchPhase.Began) }));
        Step(Frame(touches: new[] { Touch(1, 740), Touch(2, 760, TouchPhase.Began) }));
        Step(Frame(touches: new[] { Touch(1, 500) }));
        Step(Frame());
        Assert.Equal(200, _sheet.Bounds.Height);
        Step(Frame(touches: new[] { Touch(3, 760, TouchPhase.Began) }));
        Step(Frame(touches: new[] { Touch(3, 700) }));
        Assert.Equal(260, _sheet.Bounds.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Malformed_and_non_begin_touch_starts_remain_rejected(int kind)
    {
        var contacts = kind switch
        {
            0 => new[] { Touch(1, 760) },
            1 => new[] { Touch(1, float.NaN, TouchPhase.Began) },
            2 => new[] { Touch(1, float.PositiveInfinity, TouchPhase.Began) },
            _ => new[] { Touch(1, 760, TouchPhase.Began), Touch(1, 760, TouchPhase.Began) }
        };
        Step(Frame(touches: contacts));
        Step(Frame(touches: new[] { Touch(1, 500) }));
        Step(Frame());
        Assert.Equal(200, _sheet.Bounds.Height);
        Assert.Equal("peek", _sheet.TargetDetentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Blocked_start_stays_rejected_after_blocker_removal(bool touch)
    {
        _input.Update(touch ? Frame(touches: new[] { Touch(1, 760, TouchPhase.Began) })
            : Frame(new(400, 760), down: true, press: true));
        _input.BlockInputRegion(Available);
        _sheet.Update(_input, Available, default, 1000, .01f);
        Step(touch ? Frame(touches: new[] { Touch(1, 500) }) : Frame(new(400, 500), down: true));
        Step(Frame());
        Assert.Equal(200, _sheet.Bounds.Height);
        Assert.Equal("peek", _sheet.TargetDetentId);
    }

    [Fact]
    public void Touch_ownership_does_not_duplicate_emulated_mouse_movement()
    {
        Step(Frame(new(400, 760), down: true, press: true,
            touches: new[] { Touch(1, 760, TouchPhase.Began) }));
        Step(Frame(new(400, 700), down: true, touches: new[] { Touch(1, 700) }));
        Assert.Equal(260, _sheet.Bounds.Height);
        Step(Frame(new(400, 650), down: true), 0);
        Step(Frame(new(400, 600), down: true), 0);
        Assert.Equal(260, _sheet.Bounds.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Lifecycle_cancellation_inhibits_held_mouse_and_allows_fresh_press_after_release(int cause)
    {
        Step(Frame(new(400, 710), down: true, press: true));
        if (cause == 0) _sheet.CancelGestures();
        Step(Frame(new(400, 650), down: true, focused: cause != 1, captured: cause == 2),
            suppressed: cause == 3, bounds: cause == 4 ? new Rect(100, 100, 800, 700) : null);
        Step(Frame(new(400, 500), down: true));
        Step(Frame(new(400, 710)), suppressed: true);
        Assert.Equal("peek", _sheet.TargetDetentId);
        Assert.Equal(200, _sheet.Bounds.Height);
        Step(Frame(new(400, 710), down: true, press: true));
        Step(Frame(new(400, 610), down: true));
        Assert.Equal(300, _sheet.Bounds.Height);
    }

    [Fact]
    public void Unfocused_synthetic_clear_does_not_release_lifecycle_inhibition()
    {
        Step(Frame(new(400, 710), down: true, press: true));
        Step(Frame(focused: false));
        Step(Frame(new(400, 710), down: true, press: true));
        Step(Frame(new(400, 610), down: true));
        Assert.Equal(200, _sheet.Bounds.Height);
        Step(Frame());
        Step(Frame(new(400, 710), down: true, press: true));
        Step(Frame(new(400, 610), down: true));
        Assert.Equal(300, _sheet.Bounds.Height);
    }

    [Fact]
    public void New_press_interrupts_snap_without_an_idle_animation_step()
    {
        _sheet.SetDetent("full", true);
        Step(Frame(), .09f);
        Assert.Equal(550, _sheet.Bounds.Height, 3);
        Step(Frame(new(400, 360), down: true, press: true), .09f);
        Assert.Equal(550, _sheet.Bounds.Height, 3);
        Step(Frame(new(400, 350), down: true));
        Assert.Equal(560, _sheet.Bounds.Height, 3);
    }

    [Fact]
    public void Stationary_release_chooses_nearest_height_without_stale_fling()
    {
        Step(Frame(new(400, 710), down: true, press: true));
        Step(Frame(new(400, 690), down: true));
        Step(Frame(new(400, 690)), .08f);
        Assert.Equal("peek", _sheet.TargetDetentId);
    }

    [Fact]
    public void Discrete_wheel_only_scrolls_expanded_content_and_never_collapses_sheet()
    {
        Step(Frame(samples: new[] { Wheel(-1, 760) }));
        Assert.Equal(200, _sheet.Bounds.Height);
        Assert.Equal(0, _sheet.ScrollOffset);
        _sheet.SetDetent("full", false);
        Step(Frame(samples: new[] { Wheel(-1, 400) }));
        Assert.Equal(40, _sheet.ScrollOffset);
        Step(Frame(samples: new[] { Wheel(10, 400) }));
        Assert.Equal(0, _sheet.ScrollOffset);
        Assert.Equal(600, _sheet.Bounds.Height);
        Assert.Equal("full", _sheet.TargetDetentId);
        Step(Frame(samples: new[] { Wheel(-1, 310) }));
        Assert.Equal(0, _sheet.ScrollOffset);
    }

    [Fact]
    public void Precise_scroll_transfers_motion_and_preserves_native_momentum_admission()
    {
        Step(Frame(samples: new[] { Precise(-430, 760, PointerGestureInputPhase.Began) }));
        Assert.Equal(600, _sheet.Bounds.Height);
        Assert.Equal(30, _sheet.ScrollOffset);
        Step(Frame(samples: new[] { Precise(0, 760, PointerGestureInputPhase.Ended) }));
        Step(Frame(samples: new[] { Precise(50, 10, PointerGestureInputPhase.Changed, true) }));
        Assert.Equal(580, _sheet.Bounds.Height);
        Assert.Equal(0, _sheet.ScrollOffset);
        Step(Frame(), 1);
        Assert.Equal(580, _sheet.Bounds.Height);
    }

    [Fact]
    public void Rejected_native_begin_and_orphan_momentum_never_gain_admission()
    {
        Step(Frame(samples: new[] { Precise(-30, 10, PointerGestureInputPhase.Began) }));
        Step(Frame(samples: new[] { Precise(-30, 760, PointerGestureInputPhase.Changed) }));
        Step(Frame(samples: new[] { Precise(0, 760, PointerGestureInputPhase.Ended) }));
        Step(Frame(samples: new[] { Precise(-30, 760, PointerGestureInputPhase.Changed, true) }));
        Assert.Equal(200, _sheet.Bounds.Height);
        _sheet.CancelGestures();
        Step(Frame(samples: new[] { Precise(-30, 760, PointerGestureInputPhase.Changed, true) }));
        Assert.Equal(200, _sheet.Bounds.Height);
    }

    [Fact]
    public void Precise_scroll_maps_points_as_vectors_and_ignores_legacy_summary()
    {
        var mapping = new DesignViewport(1000, 1000);
        mapping.Update(2000, 2000);
        Step(Frame(scale: new(2, 2)), mapping: mapping);
        Step(Frame(samples: new[] { Precise(-20, 1520) }, scale: new(2, 2), scroll: -100), mapping: mapping);
        Assert.Equal(220, _sheet.Bounds.Height);
    }

    [Fact]
    public void Authoritative_empty_stream_ignores_legacy_wheel_summary()
    {
        _sheet.SetDetent("full", false);
        Step(Frame(new(400, 400), scroll: -1));
        Assert.Equal(0, _sheet.ScrollOffset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Admitted_sheet_gesture_never_moves_or_taps_lower_map_even_after_dismissal(bool touch)
    {
        var map = new PannableCanvas { Viewport = Available, ContentBounds = new(0, 0, 2000, 2000) };
        map.Camera.Position = new(1000, 1000);
        void UpdateMap(InputState frame)
        {
            Step(frame);
            map.Update(_input, .01f);
            Assert.False(map.TryGetGestureTap(out _));
            Assert.Equal(new Vector2(1000, 1000), map.Camera.Position);
        }
        UpdateMap(touch ? Frame(touches: new[] { Touch(1, 710, TouchPhase.Began) })
            : Frame(new(400, 710), down: true, press: true));
        _sheet.SetDetent("closed", false);
        UpdateMap(touch ? Frame(touches: new[] { Touch(1, 850) }) : Frame(new(400, 850), down: true));
        UpdateMap(touch ? Frame(touches: new[] { Touch(1, 850, TouchPhase.Ended) }) : Frame(new(400, 850)));
        Assert.Equal(!touch, _input.Pointer.IsConsumed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Channel_sheet_mouse_release_does_not_block_next_independent_map_gesture(int kind)
    {
        var map = new PannableCanvas { Viewport = Available, ContentBounds = new(0, 0, 2000, 2000) };
        map.Camera.Position = new(1000, 1000);
        Step(Frame(new(400, 710), press: true));
        map.Update(_input, .01f);
        Assert.False(map.TryGetGestureTap(out _));
        Assert.True(_input.Pointer.IsConsumed);
        switch (kind)
        {
            case 0: Step(Frame(samples: new[] { Wheel(1, 200) })); break;
            case 1: Step(Frame(samples: new[] { Precise(10, 200) })); break;
            case 2:
                Step(Frame(samples: new[] { new PointerGestureSample(PointerGestureKind.Magnify,
                    PointerGestureSource.Synthetic, PointerGestureInputPhase.None, new(400, 200), scaleFactor: 2) }));
                break;
            default:
                Step(Frame(touches: new[] { Touch(1, 200, TouchPhase.Began) }));
                map.Update(_input, .01f);
                Step(Frame(touches: new[] { Touch(1, 220) }));
                break;
        }
        map.Update(_input, .01f);
        Assert.Equal(kind == 2 ? 2 : 1, map.Camera.Zoom);
        if (kind != 2) Assert.Equal(new Vector2(1000, kind == 0 ? 999.5f : kind == 1 ? 990 : 980), map.Camera.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Channel_stale_left_consumption_does_not_block_new_sheet_touch_or_precise(bool touch)
    {
        _input.Update(Frame(new(400, 200), down: true, press: true));
        _input.Pointer.ConsumeGesture();
        _sheet.Update(_input, Available, default, 1000, .01f);
        Step(Frame(new(400, 200)));
        Assert.True(_input.Pointer.IsConsumed);
        if (touch)
        {
            Step(Frame(touches: new[] { Touch(1, 710, TouchPhase.Began) }));
            Step(Frame(touches: new[] { Touch(1, 690) }));
        }
        else Step(Frame(samples: new[] { Precise(-20, 760) }));
        Assert.Equal(220, _sheet.Bounds.Height);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_collapse_preserves_admission_region_for_lower_map(bool ordered)
    {
        var map = new PannableCanvas { Viewport = Available, ContentBounds = new(0, 0, 2000, 2000) };
        map.Camera.Position = new(1000, 1000);
        Step(Frame(samples: ordered ? new[] { Precise(-400, 760), Precise(600, 400) }
            : new[] { Precise(200, 760, PointerGestureInputPhase.Began) }));
        Assert.Equal(0, _sheet.Bounds.Height);
        map.Update(_input, .01f);
        Assert.Equal(new Vector2(1000, 1000), map.Camera.Position);
    }

    [Fact]
    public void Same_frame_mouse_dismissal_cannot_tap_lower_map()
    {
        var sheet = new DraggableBottomSheet(new[]
        {
            BottomSheetDetent.Fixed("closed", 0, true), BottomSheetDetent.Fixed("peek", 200)
        }, "peek", new() { HandleToggleDetentIds = new[] { "closed", "peek" } });
        var map = new PannableCanvas { Viewport = Available, ContentBounds = new(0, 0, 2000, 2000) };
        sheet.Update(_input, Available, default, 1000, 0);
        _input.Update(Frame(new(400, 710), press: true));
        sheet.Update(_input, Available, default, 1000, .01f);
        sheet.SetDetent("closed", false);
        map.Update(_input, .01f);
        Assert.False(map.TryGetGestureTap(out _));
        Assert.False(_input.IsTapIn(Available));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void Zoom_intent_is_reserved_without_resizing_or_scrolling_sheet(int kind, bool handle)
    {
        _sheet.SetDetent("full", false);
        var map = new PannableCanvas { Viewport = Available, ContentBounds = new(0, 0, 2000, 2000) };
        map.Camera.Position = new(1000, 1000);
        var sample = new PointerGestureSample(kind == 0 ? PointerGestureKind.Wheel
            : kind == 1 ? PointerGestureKind.PreciseScroll : PointerGestureKind.Magnify,
            PointerGestureSource.Synthetic, PointerGestureInputPhase.None, new(400, handle ? 310 : 400),
            new(0, kind == 0 ? -1 : 50), scaleFactor: 2, commandDown: true);
        Step(Frame(samples: new[] { sample }));
        map.Update(_input, .01f);
        Assert.Equal(600, _sheet.Bounds.Height);
        Assert.Equal(0, _sheet.ScrollOffset);
        Assert.Equal(1, map.Camera.Zoom);
        Assert.Equal(new Vector2(1000, 1000), map.Camera.Position);
    }

    [Fact]
    public void Command_precise_begin_cannot_admit_later_unmodified_changed_sample()
    {
        Step(Frame(samples: new[] { new PointerGestureSample(PointerGestureKind.PreciseScroll,
            PointerGestureSource.Synthetic, PointerGestureInputPhase.Began, new(400, 760), new(0, -20), commandDown: true) }));
        Step(Frame(samples: new[] { Precise(-20, 760, PointerGestureInputPhase.Changed) }));
        Assert.Equal(200, _sheet.Bounds.Height);
    }

    [Theory]
    [InlineData(Key.LeftControl, 0)]
    [InlineData(Key.RightControl, 0)]
    [InlineData(Key.LeftSuper, 0)]
    [InlineData(Key.RightSuper, 0)]
    [InlineData(Key.A, 40)]
    public void Postcheck_legacy_wheel_modifier_preserves_zoom_intent(Key key, float expectedOffset)
    {
        _sheet.SetDetent("full", false);
        var legacy = new InputState(new HashSet<Key> { key }, new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(), new(400, 400), Vector2.Zero, -1, 2000, 2000);
        Step(legacy);
        Assert.False(_input.Gestures.IsAvailable);
        Assert.Equal(600, _sheet.Bounds.Height);
        Assert.Equal(expectedOffset, _sheet.ScrollOffset);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Postcheck_native_end_settles_owned_height_without_fling_or_release_frame_advance(bool momentum, bool command)
    {
        Step(Frame(samples: new[] { Precise(-100, 760, PointerGestureInputPhase.Began) }));
        Assert.Equal(300, _sheet.Bounds.Height);
        if (momentum)
        {
            Step(Frame(samples: new[] { Precise(0, 760, PointerGestureInputPhase.Ended) }));
            Step(Frame(samples: new[] { Precise(-250, 760, PointerGestureInputPhase.Changed, true) }));
        }
        float beforeEnd = momentum ? 550 : 300;
        Step(Frame(samples: new[] { Precise(0, 760, PointerGestureInputPhase.Ended, momentum, command) }), .18f);
        Assert.Equal(beforeEnd, _sheet.Bounds.Height);
        Step(Frame(), 0);
        Assert.Equal(beforeEnd, _sheet.Bounds.Height);
        Step(Frame(), .18f);
        Assert.Equal(momentum ? 600 : 200, _sheet.Bounds.Height);
        Assert.Equal(momentum ? "full" : "peek", _sheet.CurrentDetentId);
    }

    [Theory]
    [InlineData(PointerGestureInputPhase.Ended)]
    [InlineData(PointerGestureInputPhase.Cancelled)]
    public void Postcheck_rejected_command_stream_does_not_interrupt_or_settle_unrelated_snap(PointerGestureInputPhase terminal)
    {
        _sheet.SetDetent("full", true);
        Step(Frame(samples: new[] { Precise(-20, 760, PointerGestureInputPhase.Began, command: true) }), 0);
        Step(Frame(samples: new[] { Precise(-20, 760, PointerGestureInputPhase.Changed, command: true) }), 0);
        Step(Frame(samples: new[] { Precise(0, 760, terminal, command: true) }), 0);
        Assert.Equal(200, _sheet.Bounds.Height);
        Step(Frame(), .18f);
        Assert.Equal(600, _sheet.Bounds.Height);
        Assert.Equal("full", _sheet.CurrentDetentId);
    }

    [Fact]
    public void Sheet_reserves_visible_region_for_fresh_lower_map_start()
    {
        _input.Update(Frame(new(400, 760), down: true, press: true));
        _sheet.Update(_input, Available, default, 1000, .01f);
        Assert.True(_input.IsInputBlocked(new(400, 760)));
        Assert.False(_input.IsInputBlocked(new(400, 650)));
    }

    void Step(InputState state, float dt = .01f, SafeAreaInsets safe = default, bool suppressed = false,
        Rect? bounds = null, IDesignViewport? mapping = null)
    {
        _input.Update(state, mapping);
        if (suppressed) _input.SuppressPointerInput();
        _sheet.Update(_input, bounds ?? Available, safe, 1000, dt);
    }

    static InputState Frame(Vector2? position = null, bool down = false, bool press = false,
        PointerGestureSample[]? samples = null, TouchPoint[]? touches = null, Vector2? scale = null,
        bool focused = true, bool captured = false, float scroll = 0) =>
        new(new PointerGestureFrame(samples ?? Array.Empty<PointerGestureSample>()), 0, Vector2.Zero,
            scale ?? Vector2.One, new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            down ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>(),
            press ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>(),
            position ?? new(400, 760), Vector2.Zero, scroll, 2000, 2000, touches: touches,
            windowFocused: focused, pointerCaptured: captured);
    static TouchPoint Touch(long id, float y, TouchPhase phase = TouchPhase.Moved) => new(id, new(400, y), phase);
    static PointerGestureSample Wheel(float y, float anchorY) => new(PointerGestureKind.Wheel,
        PointerGestureSource.Synthetic, PointerGestureInputPhase.None, new(400, anchorY), new(0, y));
    static PointerGestureSample Precise(float y, float anchorY,
        PointerGestureInputPhase phase = PointerGestureInputPhase.None, bool momentum = false, bool command = false) =>
        new(PointerGestureKind.PreciseScroll, PointerGestureSource.Synthetic, phase,
            new(anchorY == 1520 ? 800 : 400, anchorY), new(0, y), isMomentum: momentum, commandDown: command);
}
