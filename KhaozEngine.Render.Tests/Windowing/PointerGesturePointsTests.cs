using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class PointerGesturePointsTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    public void Threshold_replay_and_continuing_drag_use_the_same_window_points(float x, float y)
    {
        var rig = new PointerPointsTestRig(new Vector2(x, y));
        var gesture = new PointerGesture(rig.Button);
        gesture.Advance(rig.Sample(true), false);
        gesture.Advance(rig.Sample(true, new Vector2(3, 1)), false);
        Assert.Equal(PointerGesturePhase.Pending, gesture.Phase);
        Assert.Equal(Vector2.Zero, gesture.DragDelta);

        gesture.Advance(rig.Sample(true, new Vector2(2, 1)), false);
        Assert.Equal(PointerGesturePhase.Dragging, gesture.Phase);
        Assert.Equal(new Vector2(5, 2), gesture.DragDelta);
        gesture.Advance(rig.Sample(true, new Vector2(1, 0)), false);
        Assert.Equal(new Vector2(1, 0), gesture.DragDelta);
        gesture.Advance(rig.Sample(false), false);
        Assert.False(gesture.TapThisFrame);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    public void A_small_click_keeps_the_framebuffer_press_origin(float x, float y)
    {
        var rig = new PointerPointsTestRig(new Vector2(x, y));
        var gesture = new PointerGesture(rig.Button);
        var press = rig.Sample(true);
        gesture.Advance(press, false);
        gesture.Advance(rig.Sample(true, new Vector2(2.5f, 0)), false);
        gesture.Advance(rig.Sample(false), false);
        Assert.True(gesture.TapThisFrame);
        Assert.Equal(new Vector2(100 * x, 150 * y), gesture.TapPosition);
        Assert.Equal(press.MousePosition, gesture.TapPosition);
    }

    [Fact]
    public void Capture_rebases_without_changing_pending_travel_or_replay_units()
    {
        var rig = new PointerPointsTestRig(new Vector2(2));
        var gesture = new PointerGesture(rig.Button);
        gesture.Advance(rig.Sample(true), false);
        gesture.Advance(rig.Sample(true, new Vector2(3, 0)), false);
        Assert.Equal(PointerGesturePhase.Pending, gesture.Phase);
        var start = rig.Sample(true, captured: true);
        gesture.Advance(start, false);
        Assert.Equal(Vector2.Zero, gesture.DragDelta);
        var held = rig.Sample(true, new Vector2(1, 0), captured: true);
        gesture.Advance(held, false);
        Assert.Equal(start.MousePosition, held.MousePosition);
        Assert.Equal(PointerGesturePhase.Pending, gesture.Phase);
        gesture.Advance(rig.Sample(true, new Vector2(1, 0), captured: true), false);
        Assert.Equal(new Vector2(5, 0), gesture.DragDelta);
        gesture.Advance(rig.Sample(true, new Vector2(-20, 0)), false);
        Assert.Equal(Vector2.Zero, gesture.DragDelta);
        gesture.Advance(rig.Sample(true, new Vector2(1, 0)), false);
        Assert.Equal(new Vector2(1, 0), gesture.DragDelta);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Immediate_drag_uses_points_and_resumes_after_ui_blocking(float threshold)
    {
        var rig = new PointerPointsTestRig(new Vector2(2, 3));
        var gesture = new PointerGesture(rig.Button, threshold);
        gesture.Advance(rig.Sample(true, new Vector2(1, 2)), false);
        Assert.Equal(new Vector2(1, 2), gesture.DragDelta);
        gesture.Advance(rig.Sample(true, new Vector2(5, 1)), true);
        Assert.Equal(Vector2.Zero, gesture.DragDelta);
        gesture.Advance(rig.Sample(true, new Vector2(2, 1)), false);
        Assert.Equal(new Vector2(2, 1), gesture.DragDelta);
        gesture.Advance(rig.Sample(false), false);
        Assert.False(gesture.TapThisFrame);
    }

    [Fact]
    public void A_scale_change_without_logical_motion_cannot_turn_a_click_into_a_drag()
    {
        var rig = new PointerPointsTestRig(Vector2.One);
        var gesture = new PointerGesture(rig.Button);
        gesture.Advance(rig.Sample(true), false);
        rig.Scale = new Vector2(2, 3);
        gesture.Advance(rig.Sample(true), false);
        Assert.Equal(PointerGesturePhase.Pending, gesture.Phase);
        gesture.Advance(rig.Sample(false), false);
        Assert.True(gesture.TapThisFrame);
        Assert.Equal(new Vector2(100, 150), gesture.TapPosition);
    }

    [Fact]
    public void Crossing_is_strict_and_path_length_still_counts_a_wiggle()
    {
        var rig = new PointerPointsTestRig(new Vector2(2, 3));
        var gesture = new PointerGesture(rig.Button);
        gesture.Advance(rig.Sample(true), false);
        gesture.Advance(rig.Sample(true, new Vector2(4, 0)), false);
        Assert.Equal(PointerGesturePhase.Pending, gesture.Phase);
        gesture.Advance(rig.Sample(true, new Vector2(-4, 0)), false);
        Assert.Equal(PointerGesturePhase.Dragging, gesture.Phase);
        Assert.Equal(Vector2.Zero, gesture.DragDelta);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_blocked_press_stays_inert_and_a_mid_drag_block_never_taps(bool ui)
    {
        var rig = new PointerPointsTestRig(new Vector2(2, 3));
        var gesture = new PointerGesture(rig.Button);
        gesture.Advance(rig.Sample(true, focused: ui), ui);
        gesture.Advance(rig.Sample(true, new Vector2(10, 0)), false);
        Assert.Equal(PointerGesturePhase.Idle, gesture.Phase);
        Assert.Equal(Vector2.Zero, gesture.DragDelta);
        gesture.Advance(rig.Sample(false), false);
        Assert.False(gesture.TapThisFrame);
        gesture.Advance(rig.Sample(true), false);
        gesture.Advance(rig.Sample(true, new Vector2(5, 0)), false);
        Assert.Equal(PointerGesturePhase.Dragging, gesture.Phase);
        gesture.Advance(rig.Sample(true, focused: ui), ui);
        Assert.Equal(PointerGesturePhase.Idle, gesture.Phase);
        gesture.Advance(rig.Sample(false), false);
        Assert.False(gesture.TapThisFrame);
    }
}
