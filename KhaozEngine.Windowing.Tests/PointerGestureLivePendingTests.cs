using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class PointerGestureLivePendingTests
{
    Vector2 _cursor = new(100, 50);

    InputState Frame(bool down, Vector2 delta = default, bool focused = true)
    {
        _cursor += delta;
        var keys = new HashSet<Key>();
        var buttons = new HashSet<MouseButton>();
        return new InputState(delta, new(2, 3), keys, keys, keys,
            down ? new HashSet<MouseButton> { MouseButton.Right } : buttons,
            buttons, _cursor, delta * new Vector2(2, 3), 0, 800, 600, windowFocused: focused);
    }

    static PointerGesture New(bool tolerance) => tolerance
        ? new(MouseButton.Right, new PointerTapTolerance(6, 0.25f, 10)) { LivePendingTravel = true }
        : new(MouseButton.Right, 6) { LivePendingTravel = true };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Live_pending_motion_preserves_an_in_tolerance_tap(bool tolerance)
    {
        var gesture = New(tolerance);
        gesture.Advance(Frame(true, new(1, 1)), false, 0.125f);
        Assert.Equal(Vector2.Zero, gesture.DragDelta);
        gesture.Advance(Frame(true, new(2, 1)), false, 0.125f);
        Assert.Equal(PointerGesturePhase.Pending, gesture.Phase);
        Assert.Equal(new Vector2(2, 1), gesture.DragDelta);
        gesture.Advance(Frame(false, new(99, 99)), false, 0.5f);
        Assert.True(gesture.TapThisFrame);
        Assert.Equal(new Vector2(101, 51), gesture.TapPosition);
        Assert.Equal(Vector2.Zero, gesture.DragDelta);
        gesture.Advance(Frame(false), false, 0);
        Assert.False(gesture.TapThisFrame);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Crossing_delivers_only_its_frame_and_preserves_total_motion(bool tolerance)
    {
        var gesture = New(tolerance);
        gesture.Advance(Frame(true, new(1, 0)), false, 0);
        Vector2 delivered = gesture.DragDelta;
        gesture.Advance(Frame(true, new(2, 0)), false, 0.125f);
        delivered += gesture.DragDelta;
        gesture.Advance(Frame(true, new(5, 1)), false, 0.125f);
        Assert.Equal(PointerGesturePhase.Dragging, gesture.Phase);
        Assert.Equal(new Vector2(5, 1), gesture.DragDelta);
        delivered += gesture.DragDelta;
        gesture.Advance(Frame(true, new(-1, 2)), false, 0.125f);
        delivered += gesture.DragDelta;
        gesture.Advance(Frame(false), false, 0);
        Assert.False(gesture.TapThisFrame);
        Assert.Equal(new Vector2(6, 3), delivered);
    }

    [Fact]
    public void Grace_crossing_ignores_the_catch_up_cap()
    {
        var gesture = new PointerGesture(MouseButton.Right,
            new PointerTapTolerance(6, 0.25f, 10) { CatchUpLimitPoints = 0 })
        { LivePendingTravel = true };
        gesture.Advance(Frame(true, new(20, 0)), false, 0);
        Assert.Equal(PointerGesturePhase.Pending, gesture.Phase);
        Assert.Equal(Vector2.Zero, gesture.DragDelta);
        gesture.Advance(Frame(true, new(4, 0)), false, 0.125f);
        Assert.Equal(new Vector2(4, 0), gesture.DragDelta);
        gesture.Advance(Frame(true, new(3, 0)), false, 0.125f);
        Assert.Equal(PointerGesturePhase.Dragging, gesture.Phase);
        Assert.Equal(new Vector2(3, 0), gesture.DragDelta);
    }

    [Fact]
    public void Live_travel_preserves_the_strict_press_frame_decision_without_delivering_its_motion()
    {
        var gesture = New(false);
        gesture.Advance(Frame(true, new(20, 0)), false);
        Assert.Equal(PointerGesturePhase.Dragging, gesture.Phase);
        Assert.Equal(Vector2.Zero, gesture.DragDelta);
        gesture.Advance(Frame(true, new(2, 0)), false);
        Assert.Equal(new Vector2(2, 0), gesture.DragDelta);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Blocked_or_suppressed_presses_never_deliver_live_travel(bool atPress, bool ui)
    {
        var gesture = New(true);
        if (!atPress) gesture.Advance(Frame(true), false, 0);
        gesture.Advance(Frame(true, new(3, 0), focused: ui), ui, 0.125f);
        Assert.Equal(Vector2.Zero, gesture.DragDelta);
        gesture.Advance(Frame(true, new(30, 0)), false, 0.125f);
        Assert.Equal(PointerGesturePhase.Idle, gesture.Phase);
        Assert.Equal(Vector2.Zero, gesture.DragDelta);
        gesture.Advance(Frame(false), false, 0);
        Assert.False(gesture.TapThisFrame);
        gesture.Advance(Frame(true), false, 0);
        gesture.Advance(Frame(true, new(2, 0)), false, 0.125f);
        Assert.Equal(new Vector2(2, 0), gesture.DragDelta);
    }
}
