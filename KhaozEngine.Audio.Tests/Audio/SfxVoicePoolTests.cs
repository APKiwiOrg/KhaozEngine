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
}
