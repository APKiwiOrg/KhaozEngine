using System;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class DragMotionTrackerTests
{
    [Fact]
    public void Crossing_slop_includes_travel_from_the_press_origin()
    {
        var drag = new DragMotionTracker();
        drag.Begin(new(20, 20));
        drag.Move(new(26, 20), 0.02f);
        Assert.Equal(Vector2.Zero, drag.DragDelta);
        drag.Move(new(30, 20), 0.02f);
        Assert.Equal(new Vector2(10, 0), drag.DragDelta);
        Assert.False(drag.TapEligible);
        Assert.True(drag.IsDragging);
    }

    [Fact]
    public void Release_includes_final_travel_and_stationary_hold_clears_velocity()
    {
        var drag = new DragMotionTracker();
        drag.Begin(new(20, 20));
        drag.Move(new(40, 20), 0.02f);
        drag.Release(new(45, 20), 0.01f);
        Assert.Equal(new Vector2(5, 0), drag.DragDelta);
        PanZoomTestRig.Near(new(500, 0), drag.ReleaseVelocity);
        drag.Begin(new(20, 20));
        drag.Move(new(40, 20), 0.02f);
        drag.Move(new(40, 20), 0.08f);
        drag.Release(new(40, 20), 0);
        Assert.Equal(Vector2.Zero, drag.ReleaseVelocity);
    }

    [Fact]
    public void Zero_time_moves_do_not_overwrite_velocity_or_duration()
    {
        var drag = new DragMotionTracker();
        drag.Begin(new(20, 20));
        drag.Move(new(30, 20), 0.1f);
        drag.Move(new(40, 20), 0);
        drag.Release(new(40, 20), 0);
        PanZoomTestRig.Near(new(100, 0), drag.ReleaseVelocity);
        drag.Reset();
        Assert.False(drag.IsActive);
        Assert.False(drag.TapEligible);
        Assert.Equal(Vector2.Zero, drag.ReleaseVelocity);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Invalid_time_is_rejected(float dt)
    {
        var drag = new DragMotionTracker();
        drag.Begin(Vector2.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => drag.Move(Vector2.One, dt));
    }

    [Fact]
    public void Release_can_cross_slop_without_a_prior_drag_move()
    {
        var drag = new DragMotionTracker();
        drag.Begin(new(20, 20));
        drag.Release(new(30, 20), 0.1f);
        Near(new(10, 0), drag.DragDelta);
        Assert.False(drag.TapEligible);
        Near(new(100, 0), drag.ReleaseVelocity);
    }

    [Fact]
    public void Invalid_configuration_and_position_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DragMotionTracker(dragSlop: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DragMotionTracker(tapMaxDuration: float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DragMotionTracker(stationaryReleaseCutoff: 0));
        var drag = new DragMotionTracker();
        Assert.Throws<ArgumentOutOfRangeException>(() => drag.Begin(new(float.NaN, 0)));
        drag.Begin(Vector2.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => drag.Release(new(float.PositiveInfinity, 0), 0));
    }

    static void Near(Vector2 expected, Vector2 actual) => PanZoomTestRig.Near(expected, actual);
}
