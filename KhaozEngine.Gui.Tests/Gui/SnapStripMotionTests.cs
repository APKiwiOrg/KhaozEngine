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
        motion.Advance(0.09f);
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
