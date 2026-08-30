using System;
using KhaozEngine.Audio;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class SfxVoicePoolTests
{
    [Fact]
    public void NextRoundRobinsDeterministically()
    {
        var pool = new SfxVoicePool(3);

        // Two full rotations: 0,1,2,0,1,2.
        Assert.Equal(new[] { 0, 1, 2, 0, 1, 2 }, new[]
        {
            pool.Next(), pool.Next(), pool.Next(), pool.Next(), pool.Next(), pool.Next(),
        });
    }

    [Fact]
    public void SingleVoiceAlwaysReturnsZero()
    {
        var pool = new SfxVoicePool(1);
        Assert.Equal(0, pool.Next());
        Assert.Equal(0, pool.Next());
        Assert.Equal(0, pool.Next());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveCountGuarded(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SfxVoicePool(count));
    }

    // ---- priority-aware stealing (#114) ------------------------------------------------------------------

    [Fact]
    public void StealTakesTheLowestPriorityVoice_NotWhicheverTheRotationLandedOn()
    {
        var pool = new SfxVoicePool(4);
        // The rotation is at 0, so plain Next() would take voice 0, the High cue. Voice 2 is the Low footstep.
        SfxPriority[] playing =
        {
            SfxPriority.High, SfxPriority.Normal, SfxPriority.Low, SfxPriority.Normal,
        };

        Assert.Equal(2, pool.Steal(playing));
    }

    [Fact]
    public void StealUsesTheRotationAsTheTieBreak_SoEqualVoicesGoOldestFirst()
    {
        var pool = new SfxVoicePool(4);
        SfxPriority[] allNormal =
        {
            SfxPriority.Normal, SfxPriority.Normal, SfxPriority.Normal, SfxPriority.Normal,
        };

        // With nothing to choose between, this is exactly the pre-priority round robin.
        Assert.Equal(new[] { 0, 1, 2, 3, 0 }, new[]
        {
            pool.Steal(allNormal), pool.Steal(allNormal), pool.Steal(allNormal), pool.Steal(allNormal),
            pool.Steal(allNormal),
        });
    }

    [Fact]
    public void StealPrefersTheFirstLowVoiceInRotationOrder()
    {
        var pool = new SfxVoicePool(4);
        SfxPriority[] twoLows =
        {
            SfxPriority.Low, SfxPriority.High, SfxPriority.Low, SfxPriority.High,
        };

        // Cursor at 0: voice 0 is the first Low in rotation order, then the cursor moves past it, so the second
        // steal takes the other Low rather than the same voice twice.
        Assert.Equal(0, pool.Steal(twoLows));
        Assert.Equal(2, pool.Steal(twoLows));
        Assert.Equal(0, pool.Steal(twoLows));   // wrapped: back to the first Low
    }

    [Fact]
    public void StealFallsBackToRotationWhenEveryVoiceMattersEqually()
    {
        // All High: a play still gets a voice (a dropped one-shot is a silence nothing reports), and the one it
        // takes is the rotation's, which is the oldest-allocated.
        var pool = new SfxVoicePool(3);
        SfxPriority[] allHigh = { SfxPriority.High, SfxPriority.High, SfxPriority.High };

        Assert.Equal(0, pool.Steal(allHigh));
        Assert.Equal(1, pool.Steal(allHigh));
    }

    [Fact]
    public void StealGuardsAMismatchedVoiceCount()
    {
        var pool = new SfxVoicePool(3);
        Assert.Throws<ArgumentException>(() => pool.Steal(new[] { SfxPriority.Low, SfxPriority.Low }));
    }
}
