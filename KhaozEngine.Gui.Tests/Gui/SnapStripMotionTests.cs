using System;
using System.Collections.Generic;
using KhaozEngine.Gui;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class SnapStripMotionTests
{
    [Fact]
    public void Half_duration_is_seven_eighths_and_arrival_is_exact()
    {
        var motion = Motion();
        motion.CenterOn(50);
        Assert.Equal(50, motion.TargetIndex);
        Assert.Equal(2420f, motion.TargetOffset);
        Assert.Equal(0f, motion.Offset);
        motion.Advance(0.09f);
        Assert.Equal(2117.5f, motion.Offset, 3);
        Assert.True(motion.IsAnimating);
        motion.CenterOn(50);
        motion.Advance(0.09f);
        Assert.Equal(2420f, motion.Offset);
        Assert.False(motion.IsAnimating);
    }

    [Fact]
    public void Elapsed_time_partitions_match()
    {
        var single = Motion();
        var partitioned = Motion();
        single.CenterOn(50);
        partitioned.CenterOn(50);
        single.Advance(0.09f);
        partitioned.Advance(0.03f);
        partitioned.Advance(0.03f);
        partitioned.Advance(0.03f);
        Assert.InRange(MathF.Abs(single.Offset - partitioned.Offset), 0f, 0.001f);
        Assert.True(single.IsAnimating);
        Assert.True(partitioned.IsAnimating);

        single.CenterOn(0, false);
        partitioned.CenterOn(0, false);
        single.CenterOn(50);
        partitioned.CenterOn(50);
        single.Advance(0.20f);
        partitioned.Advance(0.10f);
        partitioned.Advance(0.10f);
        Assert.Equal(2420f, single.Offset);
        Assert.Equal(2420f, partitioned.Offset);
        Assert.False(single.IsAnimating);
        Assert.False(partitioned.IsAnimating);
    }

    [Fact]
    public void Steps_skip_duplicate_clamped_targets()
    {
        var motion = Motion();
        motion.CenterOn(0, false);
        Assert.False(motion.CanStepPrevious);
        Assert.True(motion.CanStepNext);
        Assert.True(motion.StepNext(false));
        Assert.Equal(2, motion.TargetIndex);
        Assert.Equal(20f, motion.Offset);
        Assert.True(motion.StepPrevious(false));
        Assert.Equal(1, motion.TargetIndex);
        Assert.Equal(0f, motion.Offset);
        Assert.False(motion.CanStepPrevious);
        Assert.False(motion.StepPrevious());
        motion.CenterOn(99, false);
        Assert.True(motion.StepPrevious(false));
        Assert.Equal(97, motion.TargetIndex);
        Assert.Equal(4770f, motion.Offset);
        Assert.True(motion.StepNext(false));
        Assert.Equal(98, motion.TargetIndex);
        Assert.Equal(4790f, motion.Offset);
        Assert.False(motion.CanStepNext);
        Assert.False(motion.StepNext());
    }

    [Fact]
    public void Anchored_steps_reach_next_whole_pitch()
    {
        var motion = new SnapStripMotion();
        motion.Configure(552f, 100, new(69f, 0f, 0.4375f));
        motion.CenterOn(0, false);
        Assert.False(motion.CanStepPrevious);
        Assert.True(motion.CanStepNext);
        Assert.True(motion.StepNext(false));
        Assert.Equal(4, motion.TargetIndex);
        Assert.Equal(69f, motion.Offset);
        motion.CenterOn(99, false);
        Assert.Equal(6348f, motion.Offset);
        Assert.True(motion.StepPrevious(false));
        Assert.Equal(94, motion.TargetIndex);
        Assert.Equal(6279f, motion.Offset);
        Assert.True(motion.StepNext(false));
        Assert.Equal(95, motion.TargetIndex);
        Assert.Equal(6348f, motion.Offset);
        Assert.False(motion.CanStepNext);
    }

    [Fact]
    public void Rapid_steps_use_pending_target()
    {
        var motion = Motion();
        motion.CenterOn(50, false);
        Assert.True(motion.StepNext());
        Assert.True(motion.StepNext());
        Assert.Equal(52, motion.TargetIndex);
        Assert.Equal(2520f, motion.TargetOffset);
        Assert.Equal(2420f, motion.Offset);
        Assert.True(motion.StepPrevious());
        Assert.Equal(51, motion.TargetIndex);
        Assert.Equal(2470f, motion.TargetOffset);
        Assert.Equal(2420f, motion.Offset);
        motion.Advance(0.18f);
        Assert.Equal(2470f, motion.Offset);
        Assert.False(motion.IsAnimating);
    }

    [Theory]
    [InlineData(60, 2920f, 2819.6875f)]
    [InlineData(0, 0f, 264.6875f)]
    public void Retargeting_preserves_displayed_position(int index, float target, float halfway)
    {
        var motion = Motion();
        motion.CenterOn(50);
        motion.Advance(0.09f);
        Assert.Equal(2117.5f, motion.Offset);
        motion.CenterOn(index);
        Assert.Equal(2117.5f, motion.Offset);
        Assert.Equal(index, motion.TargetIndex);
        Assert.Equal(target, motion.TargetOffset);
        motion.Advance(0.09f);
        Assert.Equal(halfway, motion.Offset, 3);
        motion.Advance(0.09f);
        Assert.Equal(target, motion.Offset);
        Assert.False(motion.IsAnimating);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(99, false)]
    [InlineData(99, true)]
    public void Failed_steps_preserve_pending_target_position_and_elapsed_time(int index, bool animated)
    {
        var motion = Motion();
        motion.CenterOn(50, false);
        motion.CenterOn(index);
        motion.Advance(0.09f);
        Assert.True(motion.IsAnimating);
        var before = State(motion);
        Assert.False(index == 0 ? motion.CanStepPrevious : motion.CanStepNext);
        Assert.False(index == 0 ? motion.StepPrevious(animated) : motion.StepNext(animated));
        Assert.Equal(before, State(motion));
        motion.Advance(0.09f);
        Assert.Equal(index == 0 ? 0f : 4790f, motion.Offset);
        Assert.False(motion.IsAnimating);
    }

    [Theory]
    [InlineData(int.MinValue, 0, 0f)]
    [InlineData(1, 1, 0f)]
    [InlineData(50, 50, 2420f)]
    [InlineData(98, 98, 4790f)]
    [InlineData(int.MaxValue, 99, 4790f)]
    public void Immediate_centring_clamps_indices_without_canonicalizing_them(int index, int targetIndex, float offset)
    {
        var motion = Motion();
        motion.CenterOn(75);
        motion.Advance(0.03f);
        motion.CenterOn(index, false);
        Assert.Equal(targetIndex, motion.TargetIndex);
        Assert.Equal(offset, motion.TargetOffset);
        Assert.Equal(offset, motion.Offset);
        Assert.False(motion.IsAnimating);
        motion.Advance(0.18f);
        Assert.Equal(offset, motion.Offset);
    }

    [Fact]
    public void Immediate_identical_request_completes_pending_motion()
    {
        var motion = Motion();
        motion.CenterOn(50);
        motion.Advance(0.09f);
        motion.CenterOn(50, false);
        Assert.Equal(2420f, motion.Offset);
        Assert.False(motion.IsAnimating);
    }

    [Fact]
    public void Zero_duration_completes_commands_immediately()
    {
        var motion = Motion(0f);
        motion.CenterOn(50);
        Assert.Equal(2420f, motion.Offset);
        Assert.False(motion.IsAnimating);
        Assert.True(motion.StepNext());
        Assert.Equal(2470f, motion.Offset);
        Assert.False(motion.IsAnimating);
        Assert.True(motion.StepPrevious());
        Assert.Equal(2420f, motion.Offset);
        Assert.False(motion.IsAnimating);
    }

    [Fact]
    public void Custom_duration_controls_elapsed_motion()
    {
        var motion = Motion(0.4f);
        motion.CenterOn(50);
        motion.Advance(0.2f);
        Assert.Equal(2117.5f, motion.Offset, 3);
        Assert.True(motion.IsAnimating);
        motion.Advance(0.2f);
        Assert.Equal(2420f, motion.Offset);
        Assert.False(motion.IsAnimating);
    }

    [Fact]
    public void Zero_delta_does_not_advance_animation()
    {
        var motion = Motion();
        motion.CenterOn(50);
        motion.Advance(0.09f);
        var before = State(motion);
        motion.Advance(0f);
        Assert.Equal(before, State(motion));
        motion.Advance(0.09f);
        Assert.Equal(2420f, motion.Offset);
        Assert.False(motion.IsAnimating);
    }

    [Fact]
    public void Identical_configure_preserves_ease_and_drag()
    {
        var motion = new SnapStripMotion();
        motion.Configure(200f, 100, new(40f, 10f));
        motion.CenterOn(50);
        motion.Advance(0.09f);
        motion.Configure(200f, 100, new(40f, 10f));
        Assert.Equal(2117.5f, motion.Offset, 3);
        Assert.True(motion.IsAnimating);
        motion.Advance(0.045f);
        Assert.Equal(2382.1875f, motion.Offset);
        Assert.True(motion.IsAnimating);
        motion.Advance(0.045f);
        Assert.Equal(2420f, motion.Offset);
        motion.BeginDrag();
        motion.DragBy(-37f);
        var before = State(motion);
        motion.Configure(200f, 100, new(40f, 10f));
        Assert.Equal(before, State(motion));
        Assert.True(motion.IsDragActive);
        Assert.Equal(2457f, motion.Offset);
        motion.EndDrag();
        Assert.Equal(51, motion.TargetIndex);
        motion.Advance(0.18f);
        Assert.Equal(2470f, motion.Offset);
    }

    [Theory]
    [InlineData(0.09f)]
    [InlineData(0.18f)]
    public void Resize_and_roster_changes_keep_the_clamped_anchor(float elapsed)
    {
        var motion = Motion();
        motion.CenterOn(50);
        motion.Advance(elapsed);
        motion.Configure(300f, 100, new(40f, 10f));
        Assert.Equal(50, motion.TargetIndex);
        Assert.Equal(2370f, motion.Offset);
        Assert.Equal(2370f, motion.TargetOffset);
        Assert.False(motion.IsAnimating);
        motion.CenterOn(99, false);
        motion.Configure(200f, 10, new(40f, 10f));
        Assert.Equal(9, motion.TargetIndex);
        Assert.Equal(290f, motion.Offset);
        motion.Configure(200f, 0, new(40f, 10f));
        Assert.Equal(-1, motion.TargetIndex);
        Assert.Equal(0f, motion.Offset);
        Assert.False(motion.CanStepPrevious);
        Assert.False(motion.CanStepNext);
        motion.Configure(200f, 100, new(40f, 10f));
        Assert.Equal(0, motion.TargetIndex);
        Assert.Equal(0f, motion.Offset);
    }

    [Theory]
    [InlineData(300f, 100, 40f, 10f, 0.5f, 50, 2370f)]
    [InlineData(200f, 10, 40f, 10f, 0.5f, 9, 290f)]
    [InlineData(200f, 100, 60f, 10f, 0.5f, 50, 3430f)]
    [InlineData(200f, 100, 40f, 20f, 0.5f, 50, 2920f)]
    [InlineData(200f, 100, 40f, 10f, 0.25f, 50, 2470f)]
    public void Changed_layout_ends_moved_drag_at_previous_target(float width, int count, float itemWidth,
        float spacing, float anchor, int target, float offset)
    {
        var motion = Motion();
        motion.CenterOn(50, false);
        motion.BeginDrag();
        motion.DragBy(-101f);
        motion.Configure(width, count, new(itemWidth, spacing, anchor));
        Assert.Equal(target, motion.TargetIndex);
        Assert.Equal(offset, motion.Offset);
        Assert.Equal(offset, motion.TargetOffset);
        Assert.False(motion.IsDragActive);
        Assert.False(motion.IsAnimating);
        motion.DragBy(-37f);
        motion.EndDrag();
        motion.Advance(0.18f);
        Assert.Equal(offset, motion.Offset);
        Assert.Equal(target, motion.TargetIndex);
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(3, 2)]
    public void Empty_fitting_and_hidden_views_cannot_step(int count, int target)
    {
        var motion = Motion();
        motion.Configure(200f, count, new(40f, 10f));
        motion.CenterOn(2, false);
        Assert.Equal(target, motion.TargetIndex);
        Assert.Equal(0f, motion.Offset);
        Assert.False(motion.CanStepPrevious);
        Assert.False(motion.CanStepNext);
        Assert.False(motion.StepPrevious());
        Assert.False(motion.StepNext());
        motion.BeginDrag();
        motion.DragBy(-37f);
        motion.EndDrag();
        Assert.Equal(target, motion.TargetIndex);
        Assert.Equal(0f, motion.Offset);
        Assert.False(motion.IsDragActive);
        Assert.False(motion.IsAnimating);
        motion.Configure(200f, 100, new(40f, 10f));
        motion.CenterOn(50);
        motion.Advance(0.09f);
        motion.Configure(0f, 100, new(40f, 10f));
        Assert.Equal(50, motion.TargetIndex);
        Assert.Equal(0f, motion.Offset);
        Assert.Equal(0f, motion.TargetOffset);
        Assert.Equal(4990f, SnapStripGeometry.MaxScroll(motion.ViewportWidth, motion.ItemCount, motion.Metrics));
        Assert.False(motion.IsAnimating);
        Assert.False(motion.CanStepPrevious);
        Assert.False(motion.CanStepNext);
        Assert.False(motion.StepPrevious());
        Assert.False(motion.StepNext());
        Assert.Equal(50, motion.TargetIndex);
        motion.Configure(200f, 100, new(40f, 10f));
        Assert.Equal(50, motion.TargetIndex);
        Assert.Equal(2420f, motion.Offset);
        Assert.False(motion.IsAnimating);
    }

    [Fact]
    public void Invalid_reconfiguration_preserves_moved_drag_and_release()
    {
        var motion = Motion();
        motion.CenterOn(50, false);
        motion.BeginDrag();
        motion.DragBy(-37f);
        var before = State(motion);
        Assert.Throws<ArgumentOutOfRangeException>(() => motion.Configure(300f, 10, default));
        Assert.Equal(before, State(motion));
        motion.EndDrag();
        Assert.Equal(51, motion.TargetIndex);
        Assert.Equal(2457f, motion.Offset);
        motion.Advance(0.18f);
        Assert.Equal(2470f, motion.Offset);
        Assert.False(motion.IsAnimating);
    }

    [Fact]
    public void Finite_long_delta_arrives_exactly()
    {
        var motion = Motion();
        motion.CenterOn(50);
        motion.Advance(float.MaxValue);
        Assert.Equal(2420f, motion.Offset);
        Assert.False(motion.IsAnimating);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Invalid_duration_is_rejected(float duration)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SnapStripMotion(duration));

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Invalid_delta_is_rejected_without_changing_motion(float dt)
    {
        var motion = Motion();
        motion.CenterOn(50);
        motion.Advance(0.045f);
        var before = State(motion);
        Assert.Throws<ArgumentOutOfRangeException>(() => motion.Advance(dt));
        Assert.Equal(before, State(motion));
        motion.Advance(0.045f);
        Assert.Equal(2117.5f, motion.Offset, 3);
        motion.Advance(0.09f);
        Assert.Equal(2420f, motion.Offset);
        Assert.False(motion.IsAnimating);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapStripMotion().Advance(dt));
    }

    [Fact]
    public void Constructor_starts_empty_with_valid_metrics_and_inert_commands()
    {
        var motion = new SnapStripMotion();
        Assert.Equal(0, motion.ItemCount);
        Assert.Equal(0f, motion.ViewportWidth);
        Assert.Equal(new SnapStripMetrics(1f), motion.Metrics);
        Assert.Equal(-1, motion.TargetIndex);
        Assert.Equal(0f, motion.Offset);
        Assert.Equal(0f, motion.TargetOffset);
        Assert.False(motion.IsAnimating);
        Assert.False(motion.IsDragActive);
        Assert.False(motion.CanStepPrevious);
        Assert.False(motion.CanStepNext);
        var before = State(motion);
        motion.CenterOn(50);
        Assert.False(motion.StepPrevious());
        Assert.False(motion.StepNext());
        motion.Advance(0.18f);
        Assert.Equal(before, State(motion));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void Empty_and_fitting_strips_have_no_distinct_steps(int itemCount)
    {
        var motion = new SnapStripMotion();
        motion.Configure(200f, itemCount, new(40f, 10f));
        motion.CenterOn(99);
        Assert.Equal(itemCount == 0 ? -1 : itemCount - 1, motion.TargetIndex);
        Assert.Equal(0f, motion.TargetOffset);
        Assert.Equal(0f, motion.Offset);
        Assert.False(motion.IsAnimating);
        Assert.False(motion.IsDragActive);
        Assert.False(motion.CanStepPrevious);
        Assert.False(motion.CanStepNext);
        Assert.False(motion.StepPrevious());
        Assert.False(motion.StepNext());
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void Invalid_configuration_preserves_layout_target_and_motion(float viewportWidth, int itemCount,
        SnapStripMetrics metrics)
    {
        var motion = Motion();
        motion.CenterOn(50);
        motion.Advance(0.09f);
        var before = State(motion);
        Assert.Throws<ArgumentOutOfRangeException>(() => motion.Configure(viewportWidth, itemCount, metrics));
        Assert.Equal(before, State(motion));
        motion.Advance(0.045f);
        Assert.Equal(2382.1875f, motion.Offset);
        Assert.True(motion.IsAnimating);
        motion.Advance(0.045f);
        Assert.Equal(2420f, motion.Offset);
        Assert.False(motion.IsAnimating);
    }

    public static IEnumerable<object[]> InvalidConfigurations()
    {
        yield return new object[] { -1f, 100, new SnapStripMetrics(40f, 10f) };
        yield return new object[] { float.NaN, 100, new SnapStripMetrics(40f, 10f) };
        yield return new object[] { float.PositiveInfinity, 100, new SnapStripMetrics(40f, 10f) };
        yield return new object[] { float.NegativeInfinity, 100, new SnapStripMetrics(40f, 10f) };
        yield return new object[] { 200f, -1, new SnapStripMetrics(40f, 10f) };
        yield return new object[] { 200f, 0, default(SnapStripMetrics) };
        yield return new object[] { 200f, 100, new SnapStripMetrics(-1f) };
        yield return new object[] { 200f, 100, new SnapStripMetrics(float.NaN) };
        yield return new object[] { 200f, 100, new SnapStripMetrics(float.PositiveInfinity) };
        yield return new object[] { 200f, 100, new SnapStripMetrics(40f, -1f) };
        yield return new object[] { 200f, 100, new SnapStripMetrics(40f, float.NaN) };
        yield return new object[] { 200f, 100, new SnapStripMetrics(40f, float.PositiveInfinity) };
        yield return new object[] { 200f, 100, new SnapStripMetrics(40f, 10f, -0.1f) };
        yield return new object[] { 200f, 100, new SnapStripMetrics(40f, 10f, 1.1f) };
        yield return new object[] { 200f, 100, new SnapStripMetrics(40f, 10f, float.NaN) };
        yield return new object[] { 200f, 100, new SnapStripMetrics(40f, 10f, float.PositiveInfinity) };
        yield return new object[] { 200f, 0, new SnapStripMetrics(float.MaxValue, float.MaxValue) };
        yield return new object[] { 200f, 2, new SnapStripMetrics(float.MaxValue) };
    }

    [Fact]
    public void Drag_is_one_to_one_and_reverses_at_clamps()
    {
        var motion = Motion();
        motion.CenterOn(50, false);
        motion.BeginDrag();
        Assert.True(motion.IsDragActive);
        motion.DragBy(-37f);
        Assert.Equal(2457f, motion.Offset);
        Assert.Equal(50, motion.TargetIndex);
        Assert.Equal(2420f, motion.TargetOffset);
        motion.Advance(0.18f);
        Assert.Equal(2457f, motion.Offset);
        Assert.False(motion.IsAnimating);
        motion.DragBy(100000f);
        Assert.Equal(0f, motion.Offset);
        motion.DragBy(-1f);
        Assert.Equal(1f, motion.Offset);
        motion.DragBy(-100000f);
        Assert.Equal(4790f, motion.Offset);
        motion.DragBy(1f);
        Assert.Equal(4789f, motion.Offset);
        motion.EndDrag();
        Assert.Equal(99, motion.TargetIndex);
        Assert.Equal(4789f, motion.Offset);
        motion.Advance(0.18f);
        Assert.Equal(4790f, motion.Offset);
        Assert.False(motion.IsDragActive);
        Assert.False(motion.IsAnimating);
    }

    [Theory]
    [InlineData(2445f, 50, 2420f)]
    [InlineData(2446f, 51, 2470f)]
    [InlineData(5f, 0, 0f)]
    [InlineData(10f, 0, 0f)]
    [InlineData(10.125f, 2, 20f)]
    [InlineData(4785f, 99, 4790f)]
    [InlineData(4790f, 99, 4790f)]
    public void Release_uses_canonical_nearest_stop(float offset, int target, float stop)
    {
        var motion = Motion();
        motion.BeginDrag();
        motion.DragBy(-offset);
        motion.EndDrag();
        Assert.Equal(target, motion.TargetIndex);
        Assert.Equal(offset, motion.Offset);
        Assert.Equal(stop, motion.TargetOffset);
        Assert.False(motion.IsDragActive);
        motion.Advance(0.18f);
        Assert.Equal(stop, motion.Offset);
        Assert.False(motion.IsAnimating);
    }

    [Fact]
    public void Release_uses_configured_anchor()
    {
        var anchoredMotion = new SnapStripMotion();
        anchoredMotion.Configure(552f, 100, new(69f, 0f, 0.4375f));
        anchoredMotion.CenterOn(0, false);
        anchoredMotion.BeginDrag();
        anchoredMotion.DragBy(-40f);
        anchoredMotion.EndDrag();
        Assert.Equal(4, anchoredMotion.TargetIndex);
        Assert.Equal(40f, anchoredMotion.Offset);
        Assert.Equal(69f, anchoredMotion.TargetOffset);
        anchoredMotion.Advance(0.18f);
        Assert.Equal(69f, anchoredMotion.Offset);
    }

    [Fact]
    public void Tap_resumes_interrupted_target()
    {
        var motion = Motion();
        motion.CenterOn(50);
        motion.Advance(0.09f);
        Assert.Equal(2117.5f, motion.Offset);
        motion.BeginDrag();
        Assert.False(motion.IsAnimating);
        motion.Advance(0.18f);
        motion.DragBy(0f);
        Assert.Equal(2117.5f, motion.Offset);
        motion.EndDrag();
        Assert.Equal(2117.5f, motion.Offset);
        Assert.Equal(50, motion.TargetIndex);
        Assert.Equal(2420f, motion.TargetOffset);
        Assert.False(motion.IsDragActive);
        Assert.True(motion.IsAnimating);
        motion.Advance(0.18f);
        Assert.Equal(2420f, motion.Offset);
        Assert.False(motion.IsAnimating);
    }

    [Theory]
    [InlineData("next", 51, 2470f)]
    [InlineData("previous", 49, 2370f)]
    [InlineData("same", 50, 2420f)]
    [InlineData("center", 60, 2920f)]
    public void Commands_end_drag_without_reinterpreting_the_pending_index(string command, int target, float stop)
    {
        var motion = Motion();
        motion.CenterOn(50, false);
        motion.BeginDrag();
        motion.DragBy(-101f);
        Assert.Equal(2521f, motion.Offset);
        if (command == "next") Assert.True(motion.StepNext(false));
        else if (command == "previous") Assert.True(motion.StepPrevious(false));
        else motion.CenterOn(target, false);
        Assert.Equal(target, motion.TargetIndex);
        Assert.Equal(stop, motion.TargetOffset);
        Assert.Equal(stop, motion.Offset);
        Assert.False(motion.IsDragActive);
        Assert.False(motion.IsAnimating);
        motion.DragBy(-37f);
        motion.EndDrag();
        Assert.Equal(stop, motion.Offset);
        Assert.Equal(target, motion.TargetIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Identical_animated_command_ends_drag_and_resumes_pending_target(bool moved)
    {
        var motion = Motion();
        motion.CenterOn(50);
        motion.Advance(0.09f);
        motion.BeginDrag();
        if (moved) motion.DragBy(-101f);
        float offset = moved ? 2218.5f : 2117.5f;
        motion.CenterOn(50);
        Assert.Equal(offset, motion.Offset);
        Assert.Equal(50, motion.TargetIndex);
        Assert.False(motion.IsDragActive);
        Assert.True(motion.IsAnimating);
        motion.Advance(0.18f);
        Assert.Equal(2420f, motion.Offset);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(99, false)]
    [InlineData(99, true)]
    public void Failed_step_ends_drag_at_its_original_logical_target(int index, bool animated)
    {
        var motion = Motion();
        motion.CenterOn(index, false);
        motion.BeginDrag();
        motion.DragBy(index == 0 ? -37f : 37f);
        Assert.False(index == 0 ? motion.StepPrevious(animated) : motion.StepNext(animated));
        Assert.False(motion.IsDragActive);
        Assert.Equal(index, motion.TargetIndex);
        Assert.Equal(animated, motion.IsAnimating);
        if (animated) Assert.Equal(index == 0 ? 37f : 4753f, motion.Offset);
        motion.Advance(0.18f);
        Assert.Equal(index == 0 ? 0f : 4790f, motion.Offset);
    }

    [Fact]
    public void Cancel_moved_drag_settles_immediately_at_nearest_stop()
    {
        var motion = Motion();
        motion.CenterOn(50, false);
        motion.BeginDrag();
        motion.DragBy(-37f);
        Assert.Equal(2457f, motion.Offset);
        motion.CancelDrag();
        Assert.Equal(2470f, motion.Offset);
        Assert.Equal(2470f, motion.TargetOffset);
        Assert.Equal(51, motion.TargetIndex);
        Assert.False(motion.IsDragActive);
        Assert.False(motion.IsAnimating);
    }

    [Fact]
    public void Cancel_unmoved_drag_settles_immediately_at_pending_target()
    {
        var motion = Motion();
        motion.CenterOn(50);
        motion.Advance(0.09f);
        motion.BeginDrag();
        motion.CancelDrag();
        Assert.Equal(2420f, motion.Offset);
        Assert.Equal(50, motion.TargetIndex);
        Assert.False(motion.IsDragActive);
        Assert.False(motion.IsAnimating);
    }

    [Fact]
    public void Drag_operations_without_active_drag_preserve_existing_ease()
    {
        var motion = Motion();
        motion.CenterOn(50);
        motion.Advance(0.09f);
        var before = State(motion);
        motion.DragBy(-37f);
        motion.EndDrag();
        motion.CancelDrag();
        Assert.Equal(before, State(motion));
        motion.Advance(0.09f);
        Assert.Equal(2420f, motion.Offset);
        Assert.False(motion.IsAnimating);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Nonfinite_drag_delta_is_rejected_without_state_mutation(float delta)
    {
        var motion = Motion();
        motion.CenterOn(50);
        motion.Advance(0.09f);
        motion.BeginDrag();
        var before = State(motion);
        Assert.Throws<ArgumentOutOfRangeException>(() => motion.DragBy(delta));
        Assert.Equal(before, State(motion));
        motion.EndDrag();
        Assert.Equal(50, motion.TargetIndex);
        motion.Advance(0.18f);
        Assert.Equal(2420f, motion.Offset);
        Assert.Throws<ArgumentOutOfRangeException>(() => motion.DragBy(delta));
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(4, 3)]
    public void Drag_of_fitting_content_preserves_logical_anchor(int itemCount, int target)
    {
        var motion = new SnapStripMotion();
        motion.Configure(200f, itemCount, new(40f, 10f));
        motion.CenterOn(99, false);
        motion.BeginDrag();
        motion.DragBy(-37f);
        motion.EndDrag();
        Assert.Equal(target, motion.TargetIndex);
        Assert.Equal(0f, motion.Offset);
        Assert.False(motion.IsDragActive);
        Assert.False(motion.IsAnimating);
    }

    [Fact]
    public void Movement_back_to_origin_still_settles_nearest_stop()
    {
        var motion = Motion();
        motion.CenterOn(50);
        motion.Advance(0.09f);
        motion.BeginDrag();
        motion.DragBy(-37f);
        motion.DragBy(37f);
        motion.EndDrag();
        Assert.Equal(2117.5f, motion.Offset);
        Assert.Equal(44, motion.TargetIndex);
        Assert.Equal(2120f, motion.TargetOffset);
    }

    static SnapStripMotion Motion(float snapSeconds = 0.18f)
    {
        var motion = new SnapStripMotion(snapSeconds);
        motion.Configure(200f, 100, new(40f, 10f));
        return motion;
    }

    static object State(SnapStripMotion motion)
        => (motion.ItemCount, motion.ViewportWidth, motion.Metrics, motion.Offset, motion.TargetOffset,
            motion.TargetIndex, motion.IsAnimating, motion.IsDragActive, motion.CanStepPrevious, motion.CanStepNext);
}
