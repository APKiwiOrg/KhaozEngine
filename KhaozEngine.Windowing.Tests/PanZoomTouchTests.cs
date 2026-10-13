using System;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;
using static KhaozEngine.Tests.Windowing.PanZoomTestRig;

namespace KhaozEngine.Tests.Windowing;

public sealed class PanZoomTouchTests
{
    [Fact]
    public void Pinch_zooms_at_previous_centroid_then_pans_and_preserves_material_point()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(touches: new[] { Touch(1, 20, 20, TouchPhase.Began), Touch(2, 40, 20, TouchPhase.Began) }));
        rig.Step(Frame(touches: new[] { Touch(2, 65, 25), Touch(1, 25, 25) }));
        Near(2, rig.Target.Scale);
        Near(new(45, 25), rig.Target.Screen(new(30, 20)));
        Assert.Equal("zoom", rig.Target.Operations[0].Kind);
        Assert.Equal("pan", rig.Target.Operations[1].Kind);
    }

    [Fact]
    public void Touch_replacement_and_coincident_pair_rebase_without_jump()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20), StartTouch(2, 20, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 20, 20), Touch(2, 40, 20) }));
        Assert.Empty(rig.Target.Operations);
        rig.Step(Frame(touches: new[] { Touch(1, 20, 20), StartTouch(3, 100, 20) }));
        Assert.Empty(rig.Target.Operations);
        rig.Step(Frame(touches: new[] { Touch(3, 110, 20), Touch(1, 30, 20) }));
        Near(new(10, 0), rig.Target.Translation);
        Near(1, rig.Target.Scale);
    }

    [Fact]
    public void Second_touch_admission_checks_each_contact_and_cannot_rescue_rejected_start()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20), StartTouch(2, 250, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 10, 20), Touch(2, 50, 20) }));
        Assert.Empty(rig.Target.Operations);
        rig.Step(Frame(touches: Array.Empty<TouchPoint>()));
        rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20) }), blocked: _ => true);
        rig.Step(Frame(touches: new[] { Touch(1, 20, 20), StartTouch(2, 40, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 10, 20), Touch(2, 50, 20) }));
        Assert.Empty(rig.Target.Operations);
    }

    [Fact]
    public void More_than_two_contacts_cancel_until_all_release()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20), StartTouch(2, 40, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 20, 20), Touch(2, 40, 20), StartTouch(3, 60, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 10, 20), Touch(2, 50, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 10, 20) }));
        rig.Step(Frame(touches: Array.Empty<TouchPoint>()));
        Assert.Empty(rig.Target.Operations);
        Assert.False(rig.Controller.Tapped);
        rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 35, 20) }));
        Near(new(15, 0), rig.Target.Translation);
    }

    [Fact]
    public void Lifting_one_finger_rebases_drag_and_cannot_tap()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20), StartTouch(2, 40, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 20, 20) }));
        Assert.Empty(rig.Target.Operations);
        rig.Step(Frame(touches: new[] { Touch(1, 35, 20) }));
        Near(new(15, 0), rig.Target.Translation);
        rig.Step(Frame(touches: Array.Empty<TouchPoint>()));
        Assert.False(rig.Controller.Tapped);
    }

    [Fact]
    public void Committed_pinch_to_one_finger_has_no_fresh_slop_delay()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20), StartTouch(2, 40, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 10, 20), Touch(2, 50, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 10, 20) }));
        var before = rig.Target.Translation;
        rig.Step(Frame(touches: new[] { Touch(1, 12, 20) }));
        Near(before + new Vector2(2, 0), rig.Target.Translation);
        rig.Step(Frame(touches: Array.Empty<TouchPoint>()));
        Assert.False(rig.Controller.Tapped);
    }

    [Theory]
    [InlineData(0.05f, true)]
    [InlineData(0.11f, false)]
    public void Two_finger_tap_requires_timely_second_contact(float delay, bool expected)
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 20, 20), StartTouch(2, 40, 20) }), delay);
        rig.Step(Frame(touches: new[] { Touch(1, 20, 20, TouchPhase.Ended), Touch(2, 40, 20, TouchPhase.Ended) }), 0.05f);
        Near(expected ? 0.5f : 1, rig.Target.Scale);
        Near(new(30, 20), rig.Target.Screen(new(30, 20)));
        Assert.False(rig.Controller.Tapped);
    }

    [Fact]
    public void Staggered_two_finger_release_retains_tap_until_last_contact_ends()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20), StartTouch(2, 40, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 20, 20), Touch(2, 40, 20, TouchPhase.Ended) }), 0.05f);
        Near(1, rig.Target.Scale);
        rig.Step(Frame(touches: new[] { Touch(1, 20, 20, TouchPhase.Ended) }), 0.05f);
        Near(0.5f, rig.Target.Scale);
    }

    [Fact]
    public void Moving_either_finger_disqualifies_two_finger_tap()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20), StartTouch(2, 40, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 30, 20), Touch(2, 40, 20) }));
        float before = rig.Target.Scale;
        rig.Step(Frame(touches: Array.Empty<TouchPoint>()));
        Near(before, rig.Target.Scale);
    }

    [Fact]
    public void Touch_is_not_duplicated_as_mouse_and_touch_pinch_skips_wheel_resolver()
    {
        int resolved = 0;
        var rig = new PanZoomTestRig(new() { WheelZoomFactorResolver = (_, _) => { resolved++; return 3; } });
        rig.Step(Frame(down: true, touches: new[] { StartTouch(1, 20, 20), StartTouch(2, 40, 20) }));
        rig.Step(Frame(new(80, 20), down: true, touches: new[] { Touch(1, 10, 20), Touch(2, 50, 20) }));
        Near(2, rig.Target.Scale);
        Near(new(-30, -20), rig.Target.Translation);
        Assert.Equal(0, resolved);
    }

    [Fact]
    public void Suppression_inhibits_touches_through_held_contact_restart()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 40, 20) }), suppressed: true);
        rig.Step(Frame(touches: new[] { Touch(1, 80, 20) }));
        rig.Step(Frame(touches: Array.Empty<TouchPoint>()));
        Assert.Empty(rig.Target.Operations);
        Assert.False(rig.Controller.Tapped);
    }

    [Fact]
    public void Review_close_pair_pinch_continues_one_finger_without_fresh_slop()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20), StartTouch(2, 21, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 19.5f, 20), Touch(2, 21.5f, 20) }));
        Near(2, rig.Target.Scale);
        Near(new(-20.5f, -20), rig.Target.Translation);
        rig.Step(Frame(touches: new[] { Touch(1, 19.5f, 20), Touch(2, 21.5f, 20, TouchPhase.Ended) }));
        Near(new(-20.5f, -20), rig.Target.Translation);
        rig.Step(Frame(touches: new[] { Touch(1, 20.5f, 20) }));
        Near(new(-19.5f, -20), rig.Target.Translation);
        rig.Step(Frame(touches: new[] { Touch(1, 20.5f, 20, TouchPhase.Ended) }));
        Assert.False(rig.Controller.Tapped);
        Assert.False(rig.Controller.DoubleTapped);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Review_sub_slop_pair_effect_cannot_become_two_finger_tap(bool pinch)
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20), StartTouch(2, 21, 20) }));
        float firstX = pinch ? 19.5f : 20.5f;
        rig.Step(Frame(touches: new[] { Touch(1, firstX, 20), Touch(2, 21.5f, 20) }));
        Near(pinch ? 2 : 1, rig.Target.Scale);
        Near(pinch ? new(-20.5f, -20) : new(0.5f, 0), rig.Target.Translation);
        rig.Step(Frame(touches: new[] { Touch(1, firstX, 20, TouchPhase.Ended),
            Touch(2, 21.5f, 20, TouchPhase.Ended) }));
        Near(pinch ? 2 : 1, rig.Target.Scale);
        Assert.Single(rig.Target.Operations);
        Assert.False(rig.Controller.Tapped);
        Assert.False(rig.Controller.DoubleTapped);
    }

    [Theory]
    [InlineData(false, float.NaN)]
    [InlineData(false, float.PositiveInfinity)]
    [InlineData(true, 20)]
    public void Review_invalid_initial_contact_stays_rejected_after_its_disappearance(bool duplicate, float x)
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20), StartTouch(duplicate ? 1 : 2, x, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 40, 20) }));
        Assert.Empty(rig.Target.Operations);
        rig.Step(Frame(touches: new[] { Touch(1, 40, 20, TouchPhase.Ended) }));
        rig.Step(Frame(), 0.1f);
        Assert.Empty(rig.Target.Operations);
        Assert.False(rig.Controller.Tapped);
        rig.Step(Frame(touches: new[] { StartTouch(3, 20, 20) }));
        rig.Step(Frame(touches: new[] { Touch(3, 30, 20) }));
        Near(new(10, 0), rig.Target.Translation);
    }

    [Fact]
    public void Review_fully_invalid_start_cannot_be_rescued_by_a_later_valid_contact()
    {
        var rig = new PanZoomTestRig();
        rig.Step(Frame(touches: new[] { StartTouch(1, float.NaN, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, float.NaN, 20), StartTouch(2, 20, 20) }));
        rig.Step(Frame(touches: new[] { Touch(2, 40, 20) }));
        Assert.Empty(rig.Target.Operations);
        rig.Step(Frame(touches: new[] { Touch(2, 40, 20, TouchPhase.Ended) }));
        Assert.False(rig.Controller.Tapped);
    }
}
