using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;
using static KhaozEngine.Tests.Windowing.PanZoomTestRig;

namespace KhaozEngine.Tests.Windowing;

public sealed class PanZoomLifecycleTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Channel_consumed_left_release_does_not_block_independent_pointer_streams(int kind)
    {
        var rig = new PanZoomTestRig(new() { WheelMode = PanZoomWheelMode.Pan });
        rig.Input.Update(Frame(down: true));
        rig.Input.Pointer.ConsumeGesture();
        rig.Controller.Update(rig.Input, rig.Target, Bounds, 0);
        rig.Step(Frame());
        Assert.True(rig.Input.Pointer.IsConsumed);
        switch (kind)
        {
            case 0: rig.Step(Frame(samples: new[] { Wheel(1, new(20, 20)) })); break;
            case 1: rig.Step(Frame(samples: new[] { Precise(new(3, 4), new(20, 20)) })); break;
            case 2: rig.Step(Frame(samples: new[] { Magnify(2, new(20, 20)) })); break;
            case 3:
                rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20) }));
                rig.Step(Frame(touches: new[] { Touch(1, 40, 20) }));
                break;
            default: rig.Step(Frame(scroll: 1)); break;
        }
        Near(kind == 2 ? 2 : 1, rig.Target.Scale);
        Near(kind switch
        {
            1 => new(3, 4), 2 => new(-20, -20), 3 => new(20, 0), _ => new Vector2(0, 40)
        }, rig.Target.Translation);
    }

    [Fact]
    public void Channel_consumed_active_left_gesture_still_cannot_move_or_tap()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(down: true));
        rig.Input.Update(Frame(new(50, 20), down: true));
        rig.Input.Pointer.ConsumeGesture();
        rig.Controller.Update(rig.Input, rig.Target, Bounds, .01f);
        rig.Step(Frame(new(80, 20), down: true));
        rig.Step(Frame(new(80, 20)));
        rig.Step(Frame(), .18f);
        Assert.Empty(rig.Target.Operations);
        Assert.False(rig.Controller.Tapped);
    }

    [Fact]
    public void Blocked_start_stays_inert_after_blocker_removal()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(down: true), blocked: _ => true);
        rig.Step(Frame(new(60, 20), down: true));
        rig.Step(Frame(new(60, 20)));
        rig.Step(Frame(), 0.1f);
        Assert.Empty(rig.Target.Operations);
        Assert.False(rig.Controller.Tapped);
    }

    [Theory]
    [InlineData("suppressed")]
    [InlineData("resize")]
    [InlineData("focus")]
    [InlineData("capture")]
    [InlineData("cancel")]
    public void Interruption_inhibits_held_mouse_until_release(string cause)
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(down: true));
        rig.Step(Frame(new(40, 20), down: true));
        rig.Target.Operations.Clear();
        if (cause == "cancel") rig.Controller.Cancel();
        rig.Step(Frame(new(50, 20), down: true, windowFocused: cause != "focus",
            captured: cause == "capture", width: cause == "resize" ? 220 : 200),
            suppressed: cause == "suppressed");
        rig.Step(Frame(new(80, 20), down: true));
        rig.Step(Frame(new(80, 20)));
        rig.Step(Frame(new(80, 20)), 0.1f);
        Assert.Empty(rig.Target.Operations);
        Assert.False(rig.Controller.Tapped);
        rig.Step(Frame(new(80, 20), down: true));
        rig.Step(Frame(new(90, 20), down: true));
        Near(new(30, 0), rig.Target.Translation);
    }

    [Fact]
    public void Viewport_change_discards_animation_and_tap_history()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(press: true, release: true));
        rig.Step(Frame(samples: new[] { Wheel(1, new(20, 20)) }), 0);
        rig.Step(Frame(), 0.2f, bounds: new(0, 0, 180, 200));
        Near(1, rig.Target.Scale);
        rig.Step(Frame(press: true, release: true), bounds: new(0, 0, 180, 200));
        Assert.True(rig.Controller.Tapped);
        Assert.False(rig.Controller.DoubleTapped);
    }

    [Fact]
    public void Consumed_pointer_cannot_start_a_controller_gesture()
    {
        var input = new InputManager();
        var target = new AffinePanZoomTarget();
        var controller = new PanZoomController();
        input.Update(Frame(down: true));
        input.Pointer.ConsumeGesture();
        controller.Update(input, target, Bounds, 0);
        input.Update(Frame(new(50, 20), down: true));
        controller.Update(input, target, Bounds, 0.1f);
        Assert.Empty(target.Operations);
    }

    [Fact]
    public void Native_momentum_preserves_original_rejection_after_end()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(samples: new[] { Precise(new(0, 10), new(20, 20), PointerGestureInputPhase.Began) }),
            blocked: _ => true);
        rig.Step(Frame(samples: new[] { Precise(Vector2.Zero, new(50, 20), PointerGestureInputPhase.Ended),
            Precise(new(0, 5), new(50, 20), PointerGestureInputPhase.Began, momentum: true) }));
        Assert.Empty(rig.Target.Operations);
    }

    [Fact]
    public void Admitted_native_momentum_survives_blocker_at_current_cursor_without_extra_fling()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(samples: new[] { Precise(new(0, 10), new(20, 20), PointerGestureInputPhase.Began) }));
        rig.Step(Frame(samples: new[] { Precise(Vector2.Zero, new(250, 20), PointerGestureInputPhase.Ended),
            Precise(new(0, 5), new(250, 20), PointerGestureInputPhase.Began, momentum: true),
            Precise(new(0, 2), new(250, 20), PointerGestureInputPhase.Ended, momentum: true) }), blocked: _ => true);
        rig.Step(Frame(), 0.2f);
        Near(new(0, 17), rig.Target.Translation);
    }

    [Fact]
    public void Cancelled_native_sequence_rejects_orphan_momentum_and_changed_events()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(samples: new[] { Precise(new(0, 10), new(20, 20), PointerGestureInputPhase.Began) }));
        rig.Step(Frame(samples: new[] { Precise(Vector2.Zero, new(20, 20), PointerGestureInputPhase.Cancelled),
            Precise(new(0, 20), new(20, 20), PointerGestureInputPhase.Began, momentum: true),
            Precise(new(0, 20), new(20, 20), PointerGestureInputPhase.Changed) }));
        Near(new(0, 10), rig.Target.Translation);
        rig.Step(Frame(samples: new[] { Precise(new(0, 3), new(20, 20), PointerGestureInputPhase.Began) }));
        Near(new(0, 13), rig.Target.Translation);
    }

    [Fact]
    public void Direct_precise_input_cancels_inertia_and_finishes_wheel_first()
    {
        var rig = PanZoomControllerTests.Fling();
        rig.Step(Frame(samples: new[] { Wheel(1, new(20, 20)), Precise(new(0, 5), new(80, 20)) }), 0.1f);
        Near(new(9, 3), rig.Target.Translation);
        Near(1.1f, rig.Target.Scale);
        rig.Step(Frame(), 0.2f);
        Near(new(9, 3), rig.Target.Translation);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public void Command_precise_zoom_ignores_dpi_design_scale_and_notch_resolver(float dpi, float designScale)
    {
        var mapping = new DesignViewport(200, 200);
        mapping.Update((int)(200 * designScale), (int)(200 * designScale));
        var rig = new PanZoomTestRig(new() { WheelZoomFactor = 2, WheelZoomFactorResolver = (_, _) => 4 });
        var anchor = new Vector2(50 * designScale);
        rig.Step(Frame(samples: new[] { Precise(new(0, 3), anchor, command: true) }, scale: new(dpi)),
            0, mapping: mapping);
        Near(1.0304545f, rig.Target.Scale);
        Near(new(50, 50), rig.Target.Screen(new(50, 50)));
    }

    [Fact]
    public void Precise_pan_converts_points_once_and_explicit_overload_matches_manager()
    {
        var mapping = new DesignViewport(200, 200);
        mapping.Update(400, 400);
        var state = Frame(samples: new[] { Precise(new(3, 4), new(40, 40)), Magnify(2, new(40, 40)) },
            scale: new(2, 3), width: 400);
        var rig = new PanZoomTestRig();
        rig.Step(state, 0, mapping: mapping);
        Near(new(-14, -8), rig.Target.Translation);
        var pointer = new Pointer();
        pointer.Update(state, mapping);
        var target = new AffinePanZoomTarget();
        new PanZoomController().Update(pointer, state, target, Bounds, 0, false);
        Near(rig.Target.Translation, target.Translation);
        Near(rig.Target.Scale, target.Scale);
    }

    [Fact]
    public void Keyboard_requires_option_and_focus_and_zooms_at_viewport_centre()
    {
        var rig = new PanZoomTestRig();
        var keys = Frame(keys: new[] { Key.Right, Key.Equals });
        rig.Step(keys, 1, focused: true);
        Assert.Empty(rig.Target.Operations);
        rig.Controller.Options.KeyboardEnabled = true;
        rig.Step(keys, 1);
        Assert.Empty(rig.Target.Operations);
        rig.Step(keys, 0.25f, focused: true);
        Near(1.1892071f, rig.Target.Scale);
        Near(new(100, -18.92071f), rig.Target.Translation);
        var before = rig.Target.Translation;
        rig.Step(keys, 0, focused: true);
        Near(before, rig.Target.Translation);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Invalid_elapsed_time_is_rejected_without_target_effect(float dt)
    {
        var rig = new PanZoomTestRig();
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Step(Frame(scroll: 1), dt));
        Assert.Empty(rig.Target.Operations);
    }

    [Fact]
    public void Invalid_options_are_rejected_and_nonfinite_legacy_motion_is_ignored()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PanZoomController(new() { InertiaDecay = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PanZoomController(new() { WheelZoomFactor = float.NaN }));
        var rig = new PanZoomTestRig();
        rig.Step(Frame(scroll: float.PositiveInfinity));
        rig.Step(Frame(new(float.NaN, 0), down: true));
        Assert.Empty(rig.Target.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lifecycle_release_allows_fresh_mouse_press_after_touch(bool suppressed)
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(down: true, touches: new[] { StartTouch(1, 20, 20) }));
        rig.Step(Frame(release: true, touches: new[] { Touch(1, 20, 20, TouchPhase.Ended) }), suppressed: suppressed);
        rig.Step(Frame(down: true, press: true));
        rig.Step(Frame(new(40, 20), down: true));
        Near(new(20, 0), rig.Target.Translation);
    }

    [Theory]
    [InlineData(false, TouchPhase.Moved)]
    [InlineData(false, TouchPhase.Stationary)]
    [InlineData(true, TouchPhase.Moved)]
    [InlineData(true, TouchPhase.Stationary)]
    public void Lifecycle_unobserved_contacts_cannot_start_or_join_without_began(bool joining, TouchPhase phase)
    {
        var rig = new PanZoomTestRig();
        rig.Controller.Cancel();
        if (joining) rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 20, 20, joining ? TouchPhase.Moved : phase), Touch(2, 40, 20, phase) }));
        rig.Step(Frame(touches: new[] { Touch(1, 10, 20), Touch(2, 50, 20) }));
        rig.Step(Frame());
        Assert.Empty(rig.Target.Operations);
        Assert.False(rig.Controller.Tapped);
        rig.Step(Frame(touches: new[] { StartTouch(3, 20, 20) }));
        rig.Step(Frame(touches: new[] { Touch(3, 30, 20) }));
        Near(new(10, 0), rig.Target.Translation);
    }

    [Fact]
    public void Lifecycle_unfocused_synthetic_clear_does_not_expire_held_contact_inhibition()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(down: true, touches: new[] { StartTouch(1, 20, 20) }));
        rig.Step(Frame(release: true, windowFocused: false));
        rig.Step(Frame(down: true));
        rig.Step(Frame(new(60, 20), down: true));
        rig.Step(Frame(new(60, 20), release: true));
        Assert.Empty(rig.Target.Operations);
        Assert.False(rig.Controller.Tapped);
        rig.Step(Frame(down: true, press: true));
        rig.Step(Frame(new(40, 20), down: true));
        Near(new(20, 0), rig.Target.Translation);
    }

    [Fact]
    public void Lifecycle_release_and_reuse_of_touch_id_rebases_and_disqualifies_two_finger_tap()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20), StartTouch(2, 40, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 20, 20), Touch(2, 40, 20, TouchPhase.Ended) }));
        rig.Step(Frame(touches: new[] { Touch(1, 20, 20), StartTouch(2, 40, 20) }));
        Assert.Empty(rig.Target.Operations);
        rig.Step(Frame(touches: new[] { Touch(1, 20, 20, TouchPhase.Ended), Touch(2, 40, 20, TouchPhase.Ended) }));
        Assert.Empty(rig.Target.Operations);
        Assert.False(rig.Controller.Tapped);
    }

    [Fact]
    public void Legacy_horizontal_only_scroll_pans_through_unavailable_stream()
    {
        var accumulator = new InputAccumulator();
        accumulator.OnScroll(new Vector2(2, 0));
        var snapshot = accumulator.Snapshot(new(20, 20), true, 200, 200);
        Assert.False(snapshot.Gestures.IsAvailable);
        var rig = new PanZoomTestRig(new() { WheelMode = PanZoomWheelMode.Pan });
        rig.Step(snapshot, 0);
        Near(new(80, 0), rig.Target.Translation);
        Near(1, rig.Target.Scale);
    }

    [Theory]
    [InlineData(float.NaN, 1, false)]
    [InlineData(float.NaN, 1, true)]
    [InlineData(float.PositiveInfinity, 1, false)]
    [InlineData(float.PositiveInfinity, 1, true)]
    [InlineData(2, float.NaN, false)]
    [InlineData(2, float.NaN, true)]
    [InlineData(2, float.PositiveInfinity, false)]
    [InlineData(2, float.PositiveInfinity, true)]
    public void Legacy_horizontal_and_vertical_nonfinite_components_are_rejected(float x, float y, bool zoom)
    {
        var accumulator = new InputAccumulator();
        accumulator.OnScroll(new Vector2(x, y));
        var rig = new PanZoomTestRig(new() { WheelMode = zoom ? PanZoomWheelMode.Zoom : PanZoomWheelMode.Pan });
        rig.Step(accumulator.Snapshot(new(20, 20), true, 200, 200), 0.12f);
        Assert.Empty(rig.Target.Operations);
    }
}
