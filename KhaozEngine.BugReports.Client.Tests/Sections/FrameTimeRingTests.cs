using System;
using KhaozEngine.BugReports.Client;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client.Sections;

public sealed class FrameTimeRingTests
{
    [Fact]
    public void Snapshot_is_oldest_first_before_the_ring_fills()
    {
        var ring = new FrameTimeRing(4);
        ring.Add(1f);
        ring.Add(2f);

        Assert.Equal(new[] { 1f, 2f }, ring.Snapshot());
    }

    [Fact]
    public void A_full_ring_drops_the_oldest()
    {
        var ring = new FrameTimeRing(3);
        for (int sample = 1; sample <= 5; sample++) ring.Add(sample);

        Assert.Equal(new[] { 3f, 4f, 5f }, ring.Snapshot());
    }

    [Fact]
    public void Default_capacity_is_300()
    {
        var ring = new FrameTimeRing();
        for (int sample = 0; sample < 301; sample++) ring.Add(1f);

        Assert.Equal(300, FrameTimeRing.DefaultCapacity);
        Assert.Equal(300, ring.Snapshot().Length);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-1f)]
    [InlineData(0f)]
    public void Unwritable_samples_are_ignored(float sample)
    {
        var ring = new FrameTimeRing(3);
        ring.Add(sample);

        Assert.Empty(ring.Snapshot());
    }

    [Fact]
    public void Snapshot_is_a_copy()
    {
        var ring = new FrameTimeRing(3);
        ring.Add(1f);
        float[] snapshot = ring.Snapshot();
        ring.Add(2f);

        Assert.Equal(new[] { 1f }, snapshot);
    }

    [Fact]
    public void Capacity_below_one_throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new FrameTimeRing(0));
}
