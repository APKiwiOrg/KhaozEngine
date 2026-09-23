using System;
using System.Linq;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

/// <summary>
/// <see cref="RandomDraw"/>: a bounded draw that always costs exactly one draw, so a bound content tunes down
/// to one cannot move every roll behind it. Proven by POSITION on the seeded source, because the cost of a
/// draw has no other observable.
/// </summary>
public class RandomDrawTests
{
    const ulong Seed = 20260914UL;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ACollapsedBoundStillCostsExactlyOneDraw(int bound)
    {
        var collapsed = new SeededRandomSource(Seed);
        var oneDraw = new SeededRandomSource(Seed);

        Assert.Equal(0, RandomDraw.Below(collapsed, bound));
        _ = oneDraw.NextULong();

        ulong[] after = Enumerable.Range(0, 8).Select(_ => collapsed.NextULong()).ToArray();
        ulong[] reference = Enumerable.Range(0, 8).Select(_ => oneDraw.NextULong()).ToArray();
        Assert.Equal(reference, after);
    }

    [Fact]
    public void ACollapsedBoundLandsWhereALiveBoundDoes()
    {
        // The property a seeded replay leans on: a bound tuned from four down to one leaves the stream where
        // four left it, so every roll behind it keeps its value.
        var collapsed = new SeededRandomSource(Seed);
        var live = new SeededRandomSource(Seed);

        Assert.Equal(0, RandomDraw.Below(collapsed, 1));
        Assert.Equal(0, RandomDraw.UpTo(collapsed, 0));
        _ = RandomDraw.Below(live, 4);
        _ = RandomDraw.UpTo(live, 20);

        Assert.Equal(live.NextInt(0, 1_000_000), collapsed.NextInt(0, 1_000_000));
    }

    [Fact]
    public void BelowAnswersItsHalfOpenIntervalAndCoversBothEnds()
    {
        var source = new SeededRandomSource(Seed);
        bool sawZero = false;
        bool sawTop = false;
        for (int i = 0; i < 20_000; i++)
        {
            int drawn = RandomDraw.Below(source, 4);
            Assert.InRange(drawn, 0, 3);
            sawZero |= drawn == 0;
            sawTop |= drawn == 3;
        }

        Assert.True(sawZero, "the draw never answered its low end");
        Assert.True(sawTop, "the draw never answered the entry below its bound");
    }

    [Fact]
    public void UpToReachesItsInclusiveTop()
    {
        var source = new SeededRandomSource(Seed);
        bool sawTop = false;
        for (int i = 0; i < 20_000; i++)
        {
            int drawn = RandomDraw.UpTo(source, 3);
            Assert.InRange(drawn, 0, 3);
            sawTop |= drawn == 3;
        }

        Assert.True(sawTop, "the inclusive draw never answered its maximum, so a roll ladder is one short");
    }

    [Fact]
    public void UpToRefusesIntMaxValueAndTakesTheLargestLegalTop()
    {
        var source = new SeededRandomSource(Seed);

        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => RandomDraw.UpTo(source, int.MaxValue));
        Assert.Equal("inclusiveMax", refused.ParamName);
        Assert.InRange(RandomDraw.UpTo(source, int.MaxValue - 1), 0, int.MaxValue - 1);
    }

    [Fact]
    public void ANegativeBoundOrANullStreamIsACallerBugAndSpendsNothing()
    {
        var source = new SeededRandomSource(Seed);
        var untouched = new SeededRandomSource(Seed);

        Assert.Equal("exclusiveMax",
            Assert.Throws<ArgumentOutOfRangeException>(() => RandomDraw.Below(source, -1)).ParamName);
        Assert.Equal("exclusiveMax",
            Assert.Throws<ArgumentOutOfRangeException>(() => RandomDraw.Below(source, int.MinValue)).ParamName);
        Assert.Equal("inclusiveMax",
            Assert.Throws<ArgumentOutOfRangeException>(() => RandomDraw.UpTo(source, -1)).ParamName);
        Assert.Equal("rng", Assert.Throws<ArgumentNullException>(() => RandomDraw.Below(null!, 4)).ParamName);
        Assert.Equal("rng", Assert.Throws<ArgumentNullException>(() => RandomDraw.UpTo(null!, 4)).ParamName);

        Assert.Equal(untouched.NextULong(), source.NextULong());
    }

    [Fact]
    public void ACryptographicSourceAnswersACollapsedBound()
    {
        // A cryptographic stream has no position to keep, so its skip does nothing. The bound still answers
        // the one number it can.
        var source = new CryptographicRandomSource();

        Assert.Equal(0, RandomDraw.Below(source, 0));
        Assert.Equal(0, RandomDraw.Below(source, 1));
        Assert.Equal(0, RandomDraw.UpTo(source, 0));
    }

    [Fact]
    public void ASeededSequenceIsPinnedByValue()
    {
        // Live and collapsed bounds interleaved the way a combat round draws them: an accuracy ladder, a
        // one-wide hit, then a damage roll. The values are the seeded stream's, so a change here is a change
        // to every seeded replay. The collapsed draw is in the pin too: had it spent nothing, every value
        // after the first would be a different position's.
        var source = new SeededRandomSource(Seed);
        int[] drawn = new int[12];
        for (int round = 0; round < 4; round++)
        {
            drawn[round * 3] = RandomDraw.UpTo(source, 20);
            drawn[(round * 3) + 1] = RandomDraw.Below(source, 1);
            drawn[(round * 3) + 2] = RandomDraw.Below(source, 7);
        }

        Assert.Equal(new[] { 6, 0, 5, 19, 0, 5, 14, 0, 4, 3, 0, 3 }, drawn);
    }
}
