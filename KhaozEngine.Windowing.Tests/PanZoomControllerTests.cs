using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;
using static KhaozEngine.Tests.Windowing.PanZoomTestRig;

namespace KhaozEngine.Tests.Windowing;

public sealed class PanZoomControllerTests
{
    [Fact]
    public void Drag_starts_at_origin_and_stationary_release_has_no_inertia()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(new(20, 20), down: true));
        rig.Step(Frame(new(26, 20), down: true));
        Assert.Empty(rig.Target.Operations);
        rig.Step(Frame(new(30, 20), down: true));
        Near(new(10, 0), rig.Target.Translation);
        rig.Step(Frame(new(30, 20), down: true), 0.08f);
        rig.Step(Frame(new(30, 20)), 0);
        rig.Step(Frame(new(30, 20)), 0.1f);
        Near(new(10, 0), rig.Target.Translation);
    }

    [Fact]
    public void New_press_cancels_inertia_before_movement()
    {
        var rig = Fling();
        var before = rig.Target.Translation;
        rig.Step(Frame(new(30, 20), down: true), 0.1f);
        Near(before, rig.Target.Translation);
    }

    [Fact]
    public void Inertia_matches_analytic_oracle_and_time_partition()
    {
        var whole = Fling();
        var split = Fling();
        whole.Step(Frame(new(30, 20)), 0.1f);
        split.Step(Frame(new(30, 20)), 0.04f);
        split.Step(Frame(new(30, 20)), 0.06f);
        Near(new(16.883388f, 0), whole.Target.Translation);
        Near(whole.Target.Translation, split.Target.Translation);
    }

    internal static PanZoomTestRig Fling()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(new(20, 20), down: true));
        rig.Step(Frame(new(30, 20), down: true), 0.1f);
        rig.Step(Frame(new(30, 20)), 0);
        return rig;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_frame_tap_obeys_admission(bool blocked)
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(press: true, release: true), blocked: _ => blocked);
        Assert.Equal(!blocked, rig.Controller.Tapped);
        if (!blocked) Near(new(20, 20), rig.Controller.TapPosition);
        rig.Step(Frame());
        Assert.False(rig.Controller.Tapped);
    }

    [Fact]
    public void First_tap_reports_immediately_and_second_tap_zooms_at_its_position()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(press: true, release: true));
        Assert.True(rig.Controller.Tapped);
        rig.Step(Frame(new(25, 20), press: true, release: true), 0.1f);
        Assert.False(rig.Controller.Tapped);
        Assert.True(rig.Controller.DoubleTapped);
        Near(new(25, 20), rig.Controller.DoubleTapPosition);
        Near(new(25, 20), rig.Target.Screen(new(25, 20)));
        Near(2, rig.Target.Scale);
    }

    [Fact]
    public void Legacy_accumulator_scroll_zooms_but_enabled_empty_stream_never_replays_it()
    {
        var legacy = new InputAccumulator();
        legacy.OnScroll(new Vector2(0, 1));
        var rig = new PanZoomTestRig();
        var old = legacy.Snapshot(new(20, 20), true, 200, 200);
        Assert.False(old.Gestures.IsAvailable);
        rig.Step(old, 0.12f);
        Near(1.1f, rig.Target.Scale);
        var native = new InputAccumulator();
        native.EnablePointerGestures();
        native.OnScroll(new Vector2(0, 10));
        native.OnPointerGesture(Wheel(10, new(20, 20)));
        native.OnFocusChanged(false);
        native.OnFocusChanged(true);
        var cleared = native.Snapshot(new(20, 20), true, 200, 200);
        Assert.True(cleared.Gestures.IsAvailable);
        rig.Step(cleared, 0.12f);
        Near(1.1f, rig.Target.Scale);
    }

    [Fact]
    public void Filtered_sample_declares_authoritative_producer_before_focus_filter()
    {
        var input = new InputAccumulator();
        input.OnFocusChanged(false);
        input.OnPointerGesture(Wheel(1, new(20, 20)));
        input.OnFocusChanged(true);
        input.OnScroll(new Vector2(0, 5));
        var rig = new PanZoomTestRig();
        rig.Step(input.Snapshot(new(20, 20), true, 200, 200), 0.12f);
        Near(1, rig.Target.Scale);
    }

    [Fact]
    public void Managed_native_owner_declares_source_even_when_delivery_is_rejected()
    {
        var input = new InputAccumulator();
        using var owner = new NativeGestureRegistration(7, null, input, () => false);
        owner.OnSilkScroll(new(0, 5), new(20, 20), false);
        var snapshot = input.Snapshot(new(20, 20), true, 200, 200);
        Assert.True(snapshot.Gestures.IsAvailable);
        var rig = new PanZoomTestRig();
        rig.Step(snapshot, 0.12f);
        Near(1, rig.Target.Scale);
    }

    [Fact]
    public void Wheel_magnify_wheel_preserves_anchor_order_at_zero_time()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(samples: new[] { Wheel(1, new(20, 20)), Magnify(2, new(80, 60)),
            Wheel(2, new(140, 100)) }), 0);
        Near(2.2f, rig.Target.Scale);
        Near(new(-84, -64), rig.Target.Translation);
        rig.Step(Frame(samples: Array.Empty<PointerGestureSample>()), 0.12f);
        Near(2.662f, rig.Target.Scale);
        Near(new(-131.04f, -98.44f), rig.Target.Translation);
    }

    [Fact]
    public void Partly_animated_wheel_finishes_before_later_magnify()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(samples: new[] { Wheel(1, new(20, 20)) }), 0.06f);
        Near(MathF.Sqrt(1.1f), rig.Target.Scale);
        rig.Step(Frame(samples: new[] { Magnify(2, new(80, 60)), Wheel(2, new(140, 100)) }), 0);
        Near(2.2f, rig.Target.Scale);
        Near(new(-84, -64), rig.Target.Translation);
        rig.Step(Frame(), 0.12f);
        Near(new(-131.04f, -98.44f), rig.Target.Translation);
    }

    [Theory]
    [InlineData(0.12f, 0)]
    [InlineData(0.04f, 0.08f)]
    [InlineData(0.06f, 0.06f)]
    public void Same_anchor_wheel_factors_finish_independently_of_partitions(float first, float second)
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(samples: new[] { Wheel(1, new(40, 30)), Wheel(2, new(40, 30)) }), first);
        rig.Step(Frame(), second);
        Near(1.331f, rig.Target.Scale);
        Near(new(40, 30), rig.Target.Screen(new(40, 30)));
    }

    [Fact]
    public void Resolver_runs_once_per_active_sample_and_never_for_native_pinch()
    {
        var deltas = new List<float>();
        var rig = new PanZoomTestRig(new()
        {
            WheelZoomFactorResolver = (delta, _) =>
        {
            deltas.Add(delta);
            return 2;
        }
        });
        rig.Step(Frame(samples: new[] { Wheel(1, new(20, 20)), Wheel(1, new(20, 20)) }), 0.12f);
        Assert.Equal(new[] { 1f, 1f }, deltas);
        Near(2, rig.Target.Scale);
        rig.Step(Frame(), 0.12f);
        Assert.Equal(new[] { 1f, 1f }, deltas);
        Near(4, rig.Target.Scale);
        rig.Step(Frame(samples: new[] { Magnify(1.03f, new(20, 20)) }), 0);
        Assert.Equal(2, deltas.Count);
        Near(4.12f, rig.Target.Scale);
        var single = new PanZoomTestRig(new() { WheelZoomFactorResolver = (_, _) => 2 });
        single.Step(Frame(samples: new[] { Wheel(100, new(20, 20)) }), 0.12f);
        Near(2, single.Target.Scale);
    }

    [Fact]
    public void Wheel_pan_and_command_override_use_event_modifiers()
    {
        var rig = new PanZoomTestRig(new() { WheelMode = PanZoomWheelMode.Pan });
        rig.Step(Frame(samples: new[] { Wheel(2, new(20, 20)) }, keys: new[] { Key.LeftControl }), 0);
        Near(new(0, 80), rig.Target.Translation);
        rig.Step(Frame(samples: new[] { Wheel(1, new(20, 20), true) }), 0.12f);
        Near(1.1f, rig.Target.Scale);
        Near(new(-2, 86), rig.Target.Translation);
    }

    [Fact]
    public void Changed_wheel_policy_discards_pending_animation()
    {
        var options = new PanZoomOptions();
        var rig = new PanZoomTestRig(options);
        rig.Step(Frame(samples: new[] { Wheel(1, new(20, 20)) }), 0);
        options.WheelMode = PanZoomWheelMode.Pan;
        rig.Step(Frame(), 0.12f);
        Near(1, rig.Target.Scale);
        options.WheelMode = PanZoomWheelMode.Zoom;
        rig.Step(Frame(samples: new[] { Wheel(1, new(20, 20)) }), 0);
        options.WheelZoomFactorResolver = (_, _) => 3;
        rig.Step(Frame(), 0.12f);
        Near(1, rig.Target.Scale);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Invalid_resolver_factor_never_reaches_target(float factor)
    {
        var rig = new PanZoomTestRig(new() { WheelZoomFactorResolver = (_, _) => factor });
        rig.Step(Frame(samples: new[] { Wheel(1, new(20, 20)) }), 0.12f);
        Assert.Empty(rig.Target.Operations);
    }

    [Fact]
    public void Reversal_finishes_remaining_positive_factor_before_negative_at_clamped_target()
    {
        var input = new InputManager();
        var controller = new PanZoomController(new() { WheelZoomFactor = 2 });
        var target = new BoundedZoomTarget();
        input.Update(Frame(samples: new[] { Wheel(1, new(20, 20)) }));
        controller.Update(input, target, Bounds, 0.06f);
        Near(1.1f, target.Scale);
        input.Update(Frame(samples: new[] { Wheel(-1, new(20, 20)) }));
        controller.Update(input, target, Bounds, 0.18f);
        Near(0.55f, target.Scale);
    }

    [Fact]
    public void Identity_resolver_operations_consume_no_animation_time()
    {
        var deltas = new List<float>();
        var rig = new PanZoomTestRig(new()
        {
            WheelZoomFactorResolver = (delta, _) =>
        {
            deltas.Add(delta);
            return delta > 0 ? 1 : 0.5f;
        }
        });
        rig.Step(Frame(samples: new[] { Wheel(1, new(20, 20)), Wheel(1, new(20, 20)),
            Wheel(-1, new(20, 20)) }), 0.12f);
        Near(0.5f, rig.Target.Scale);
        Assert.Equal(new[] { 1f, 1f, -1f }, deltas);
        rig.Step(Frame(), 0.12f);
        Near(0.5f, rig.Target.Scale);
        Assert.Equal(3, deltas.Count);
    }

    [Fact]
    public void Zero_duration_custom_steps_all_complete_in_order_at_zero_time()
    {
        int resolved = 0;
        var rig = new PanZoomTestRig(new()
        {
            WheelZoomDuration = 0,
            WheelZoomFactorResolver = (_, _) => { resolved++; return 2; }
        });
        rig.Step(Frame(samples: new[] { Wheel(1, new(20, 20)), Wheel(1, new(80, 80)) }), 0);
        Near(4, rig.Target.Scale);
        Near(new(-120, -120), rig.Target.Translation);
        Assert.Equal(2, resolved);
    }

    [Fact]
    public void Zero_duration_steps_follow_timed_completion_at_exact_frame_boundary()
    {
        int resolved = 0;
        var options = new PanZoomOptions { WheelZoomFactorResolver = (_, _) => { resolved++; return 2; } };
        var rig = new PanZoomTestRig(options);
        rig.Step(Frame(samples: new[] { Wheel(1, new(20, 20)) }), 0.06f);
        options.WheelZoomDuration = 0;
        rig.Step(Frame(samples: new[] { Wheel(1, new(80, 80)), Wheel(1, new(140, 140)) }), 0.06f);
        Near(8, rig.Target.Scale);
        Near(new(-380, -380), rig.Target.Translation);
        Assert.Equal(3, resolved);
    }

    [Fact]
    public void Boundary_identity_operations_do_not_delay_following_immediate_step()
    {
        var deltas = new List<float>();
        var options = new PanZoomOptions
        {
            WheelZoomFactorResolver = (delta, _) =>
        {
            deltas.Add(delta);
            return delta == 2 ? 1 : 2;
        }
        };
        var rig = new PanZoomTestRig(options);
        rig.Step(Frame(samples: new[] { Wheel(1, new(20, 20)), Wheel(2, new(80, 80)) }), 0.06f);
        options.WheelZoomDuration = 0;
        rig.Step(Frame(samples: new[] { Wheel(1, new(140, 140)) }), 0.06f);
        Near(4, rig.Target.Scale);
        Near(new(-180, -180), rig.Target.Translation);
        Assert.Equal(new[] { 1f, 2f, 1f }, deltas);
    }

    [Theory]
    [InlineData(100, 1.1f)]
    [InlineData(-100, 0.1f)]
    public void Coalescing_preserves_individually_valid_factors_when_the_product_is_unrepresentable(
        float delta, float expected)
    {
        var input = new InputManager();
        var controller = new PanZoomController(new() { WheelZoomFactor = 2 });
        var target = new BoundedZoomTarget();
        input.Update(Frame(samples: new[] { Wheel(delta, new(20, 20)), Wheel(delta, new(20, 20)) }));
        controller.Update(input, target, Bounds, 0.24f);
        Near(expected, target.Scale);
        Assert.Equal(2, target.Factors.Count);
        Assert.All(target.Factors, factor => Assert.True(float.IsFinite(factor) && factor > 0));
    }

    sealed class BoundedZoomTarget : IPanZoomTarget
    {
        internal float Scale = 1;
        internal readonly List<float> Factors = new();
        public void PanByScreenDelta(Vector2 delta) { }
        public void ZoomByFactor(float factor, Vector2 anchor)
        {
            Factors.Add(factor);
            Scale = Math.Clamp(Scale * factor, 0.1f, 1.1f);
        }
    }
}
