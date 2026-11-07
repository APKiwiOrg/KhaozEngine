using System;
using System.Collections.Generic;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class WeightedSelectorTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    public void ExactIntervalsSkipZeroWeights(int roll, int expected)
    {
        var rng = new ScriptedSource(roll);

        Assert.True(WeightedSelector.TrySelectIndex(new[] { 0, 2, 0, 3, 0 }, rng, out int index));

        Assert.Equal(expected, index);
        Assert.Equal(new[] { (0, 5) }, rng.Bounds);
        Assert.Equal(0, rng.SkipCalls);
    }

    [Fact]
    public void EmptyOrZeroPoolReturnsFalse()
    {
        foreach (int[] weights in new[] { Array.Empty<int>(), new[] { 0 }, new[] { 0, 0, 0 } })
        {
            var rng = new ScriptedSource(0);

            Assert.False(WeightedSelector.TrySelectIndex(weights, rng, out int index));

            Assert.Equal(-1, index);
            AssertNoDraws(rng);
        }

        var defaultRng = new ScriptedSource(0);
        Assert.False(WeightedSelector.TrySelectIndex(default(ReadOnlySpan<int>), defaultRng, out int defaultIndex));
        Assert.Equal(-1, defaultIndex);
        AssertNoDraws(defaultRng);
    }

    [Fact]
    public void NullSourceIsRejectedEvenForEmptyPool()
    {
        foreach (int[] weights in new[] { Array.Empty<int>(), new[] { 0 }, new[] { -1 } })
        {
            var failure = Assert.Throws<ArgumentNullException>(
                () => WeightedSelector.TrySelectIndex(weights, null!, out _));

            Assert.Equal("rng", failure.ParamName);
        }
    }

    [Fact]
    public void InvalidPoolSpendsNothing()
    {
        int[][] pools =
        [
            [2, -1],
            [int.MinValue],
            [int.MaxValue, 1],
            [0, int.MaxValue, 0, 1]
        ];
        foreach (int[] weights in pools)
        {
            var rng = new ScriptedSource(0);

            var failure = Assert.Throws<ArgumentOutOfRangeException>(
                () => WeightedSelector.TrySelectIndex(weights, rng, out _));

            Assert.Equal("weights", failure.ParamName);
            AssertNoDraws(rng);
        }
    }

    [Fact]
    public void CollapsedAndSingletonDrawContracts()
    {
        var collapsed = new ScriptedSource(0);

        Assert.True(WeightedSelector.TrySelectIndex(new[] { 0, 1, 0 }, collapsed, out int collapsedIndex));

        Assert.Equal(1, collapsedIndex);
        Assert.Empty(collapsed.Bounds);
        Assert.Equal(1, collapsed.SkipCalls);

        var singleton = new ScriptedSource(6, expectedTotal: 7);

        Assert.True(WeightedSelector.TrySelectIndex(new[] { 0, 7, 0 }, singleton, out int singletonIndex));

        Assert.Equal(1, singletonIndex);
        Assert.Equal(new[] { (0, 7) }, singleton.Bounds);
        Assert.Equal(0, singleton.SkipCalls);
    }

    [Fact]
    public void DefaultSkipPathUsesTwoWideDraw()
    {
        var rng = new DefaultSkipSource();

        Assert.True(WeightedSelector.TrySelectIndex(new[] { 1 }, rng, out int index));

        Assert.Equal(0, index);
        Assert.Equal(new[] { (0, 2) }, rng.Bounds);
    }

    [Fact]
    public void MaximumTotalIsAccepted()
    {
        var singleton = new ScriptedSource(int.MaxValue - 1, expectedTotal: int.MaxValue);

        Assert.True(WeightedSelector.TrySelectIndex(new[] { int.MaxValue }, singleton, out int singletonIndex));

        Assert.Equal(0, singletonIndex);
        Assert.Equal(new[] { (0, int.MaxValue) }, singleton.Bounds);
        Assert.Equal(0, singleton.SkipCalls);

        var split = new ScriptedSource(int.MaxValue - 1, expectedTotal: int.MaxValue);

        Assert.True(WeightedSelector.TrySelectIndex(new[] { int.MaxValue - 1, 1 }, split, out int splitIndex));

        Assert.Equal(1, splitIndex);
        Assert.Equal(new[] { (0, int.MaxValue) }, split.Bounds);
        Assert.Equal(0, split.SkipCalls);
    }

    [Fact]
    public void CallerMaskPreservesCanonicalIndexes()
    {
        var masked = new ScriptedSource(2);

        Assert.True(WeightedSelector.TrySelectIndex(new[] { 2, 0, 3 }, masked, out int maskedIndex));

        Assert.Equal(2, maskedIndex);
        Assert.Equal(new[] { (0, 5) }, masked.Bounds);
        Assert.Equal(0, masked.SkipCalls);

        var unmasked = new ScriptedSource(2, expectedTotal: 12);

        Assert.True(WeightedSelector.TrySelectIndex(new[] { 2, 7, 3 }, unmasked, out int unmaskedIndex));

        Assert.Equal(1, unmaskedIndex);
        Assert.Equal(new[] { (0, 12) }, unmasked.Bounds);
        Assert.Equal(0, unmasked.SkipCalls);
    }

    [Fact]
    public void SlicesReturnRelativeIndexes()
    {
        int[] weights = [99, 0, 2, 0, 3, 99];
        var rng = new ScriptedSource(2);

        Assert.True(WeightedSelector.TrySelectIndex(weights.AsSpan(1, 4), rng, out int index));

        Assert.Equal(3, index);
        Assert.Equal(new[] { (0, 5) }, rng.Bounds);
        Assert.Equal(0, rng.SkipCalls);
    }

    [Fact]
    public void SelectorDoesNotWriteCallerWeights()
    {
        int[] weights = [0, 2, 0, 3, 0];
        int[] before = (int[])weights.Clone();
        var rng = new ScriptedSource(2);

        Assert.True(WeightedSelector.TrySelectIndex(weights, rng, out int index));

        Assert.Equal(3, index);
        Assert.Equal(before, weights);
        Assert.Equal(new[] { (0, 5) }, rng.Bounds);
        Assert.Equal(0, rng.SkipCalls);
    }

    [Fact]
    public void RngMutationCannotChangeCapturedPool()
    {
        int[] weights = [2, 3];
        var rng = new ScriptedSource(1, onDraw: () => Array.Clear(weights));

        Assert.True(WeightedSelector.TrySelectIndex(weights, rng, out int index));

        Assert.Equal(0, index);
        Assert.Equal(new[] { 0, 0 }, weights);
        Assert.Equal(new[] { (0, 5) }, rng.Bounds);
        Assert.Equal(0, rng.SkipCalls);

        Assert.False(WeightedSelector.TrySelectIndex(weights, rng, out int nextIndex));

        Assert.Equal(-1, nextIndex);
        Assert.Equal(new[] { (0, 5) }, rng.Bounds);
        Assert.Equal(0, rng.SkipCalls);
    }

    [Fact]
    public void SourceFailuresAreNotRetried()
    {
        var drawFailure = new ArithmeticException("draw failed");
        var throwing = new ScriptedSource(0, drawFailure: drawFailure);

        Assert.Same(drawFailure, Assert.Throws<ArithmeticException>(
            () => WeightedSelector.TrySelectIndex(new[] { 2, 3 }, throwing, out _)));
        Assert.Equal(new[] { (0, 5) }, throwing.Bounds);
        Assert.Equal(0, throwing.SkipCalls);

        foreach (int result in new[] { -1, 5 })
        {
            var invalid = new ScriptedSource(result);

            Assert.Throws<InvalidOperationException>(
                () => WeightedSelector.TrySelectIndex(new[] { 2, 3 }, invalid, out _));

            Assert.Equal(new[] { (0, 5) }, invalid.Bounds);
            Assert.Equal(0, invalid.SkipCalls);
        }

        var skipFailure = new ArithmeticException("skip failed");
        var collapsed = new ScriptedSource(0, skipFailure: skipFailure);

        Assert.Same(skipFailure, Assert.Throws<ArithmeticException>(
            () => WeightedSelector.TrySelectIndex(new[] { 1 }, collapsed, out _)));
        Assert.Empty(collapsed.Bounds);
        Assert.Equal(1, collapsed.SkipCalls);
    }

    private static void AssertNoDraws(ScriptedSource rng)
    {
        Assert.Empty(rng.Bounds);
        Assert.Equal(0, rng.SkipCalls);
    }

    private sealed class ScriptedSource(
        int result,
        int expectedTotal = 5,
        Action? onDraw = null,
        Exception? drawFailure = null,
        Exception? skipFailure = null) : IRandomSource
    {
        public List<(int Min, int Max)> Bounds { get; } = [];
        public int SkipCalls { get; private set; }

        public int NextInt(int minInclusive, int maxExclusive)
        {
            Bounds.Add((minInclusive, maxExclusive));
            Assert.Equal((0, expectedTotal), (minInclusive, maxExclusive));
            Assert.Single(Bounds);
            onDraw?.Invoke();
            if (drawFailure is not null)
                throw drawFailure;
            return result;
        }

        public void Skip()
        {
            SkipCalls++;
            if (skipFailure is not null)
                throw skipFailure;
        }

        public ulong NextULong() => throw new NotSupportedException();
        public ushort NextRollPosition() => throw new NotSupportedException();
        public void NextBytes(Span<byte> destination) => throw new NotSupportedException();
    }

    private sealed class DefaultSkipSource : IRandomSource
    {
        public List<(int Min, int Max)> Bounds { get; } = [];

        public int NextInt(int minInclusive, int maxExclusive)
        {
            Bounds.Add((minInclusive, maxExclusive));
            Assert.Equal((0, 2), (minInclusive, maxExclusive));
            return 1;
        }

        public ulong NextULong() => throw new NotSupportedException();
        public ushort NextRollPosition() => throw new NotSupportedException();
        public void NextBytes(Span<byte> destination) => throw new NotSupportedException();
    }
}
