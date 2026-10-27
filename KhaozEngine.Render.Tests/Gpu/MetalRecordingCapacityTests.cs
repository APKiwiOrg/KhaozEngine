using System;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalRecordingCapacityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("6")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("bogus")]
    public void Unset_override_preserves_the_entire_legacy_resolution(string? value)
    {
        int legacy = MetalFramesInFlight.Resolve(value, out var warning);
        var policy = MetalRecordingCapacityPolicy.Resolve(value, null);
        Assert.False(policy.Enabled);
        Assert.Equal(legacy, policy.BaseFrames);
        Assert.Equal(legacy, policy.RecordingSlots);
        Assert.Equal(warning, policy.UnrecognizedBaseValue);
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("6", 6)]
    public void Exact_override_separates_recording_capacity_from_base(string value, int slots)
    {
        var policy = MetalRecordingCapacityPolicy.Resolve("3", value);
        Assert.True(policy.Enabled);
        Assert.Equal(3, policy.BaseFrames);
        Assert.Equal(slots, policy.RecordingSlots);
        Assert.Equal(slots + 1, policy.UncommittedBound);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 3 ")]
    [InlineData("03")]
    [InlineData("+3")]
    [InlineData("4")]
    [InlineData("16")]
    [InlineData("six")]
    public void Invalid_override_is_refused(string value)
        => Assert.Throws<ArgumentException>(() => MetalRecordingCapacityPolicy.Resolve("3", value));

    [Theory]
    [InlineData("1")]
    [InlineData("6")]
    [InlineData("bogus")]
    [InlineData("17")]
    public void Enabled_override_requires_a_recognized_base_of_three(string value)
    {
        foreach (string slots in new[] { "3", "6" })
            Assert.Throws<ArgumentException>(() => MetalRecordingCapacityPolicy.Resolve(value, slots));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    public void Startup_validation_distinguishes_actual_counts_and_offscreen_absence(int slots)
    {
        var policy = MetalRecordingCapacityPolicy.Resolve("3", slots.ToString(System.Globalization.CultureInfo.InvariantCulture));
        policy.ValidateActual(slots, 3, 8);
        policy.ValidateActual(slots, null, 8);
        Assert.Throws<InvalidOperationException>(() => policy.ValidateActual(slots == 3 ? 6 : 3, 3, 8));
        Assert.Throws<InvalidOperationException>(() => policy.ValidateActual(slots, 6, 8));
        Assert.Throws<InvalidOperationException>(() => policy.ValidateActual(slots, 3, 11));
    }

    [Fact]
    public void Initialization_cleanup_runs_once_and_cannot_hide_the_original_error()
    {
        var original = new InvalidOperationException("readback");
        int cleanups = 0;
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() =>
            MetalRecordingCapacityPolicy.Initialize(() => throw original, () =>
            { cleanups++; throw new ArgumentException("cleanup"); })));
        Assert.Equal(1, cleanups);
        MetalRecordingCapacityPolicy.Initialize(() => { }, () => cleanups++);
        Assert.Equal(1, cleanups);
    }
}
