using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class GpuTimestampPoolTests
    {
        [Fact]
        public void DefaultSupportIsFalseAndIncompleteBatchesAreRefused()
        {
            Assert.False(default(GpuCapabilities).SupportsTimestamps);
            Assert.Throws<ArgumentOutOfRangeException>(() => new GpuTimestampPoolState(0));
            var state = new GpuTimestampPoolState(4);
            Assert.Throws<ArgumentOutOfRangeException>(() => state.Begin(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => state.Begin(5));
            Assert.Throws<InvalidOperationException>(() => state.Write(0));
            Assert.Throws<InvalidOperationException>(() => state.End());
            Assert.Throws<InvalidOperationException>(() => state.Resolved());

            state.Begin(2);
            Assert.Equal(2u, state.Count);
            Assert.Throws<InvalidOperationException>(() => state.Begin(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => state.Write(2));
            Assert.Throws<InvalidOperationException>(() => state.Write(1));
            state.Write(0);
            Assert.Throws<InvalidOperationException>(() => state.Write(0));
            Assert.Throws<InvalidOperationException>(() => state.End());
            Assert.Throws<InvalidOperationException>(() => state.Resolved());
            state.Write(1);
            state.End();
            Assert.Throws<InvalidOperationException>(() => state.Write(0));
            Assert.Throws<InvalidOperationException>(() => state.End());
            Assert.Throws<InvalidOperationException>(() => state.Begin(1));

            state.Resolved();
            state.Resolved(); // Repeated consumption must not make a completed batch unusable.
            Assert.Equal(2u, state.Count);
            state.Begin(1);
            Assert.Equal(1u, state.Count);
            Assert.Throws<InvalidOperationException>(() => state.End());
            state.Write(0);
            state.End();
            state.Resolved();
        }

        [Fact]
        public void WrappedCountersNormalizeWithoutInventingTicks()
        {
            ulong[] output = { 99ul, 99ul, 99ul };
            Assert.True(GpuTimestampResults.TryNormalize(new ulong[] { 250ul, 5ul, 9ul }, 8, output));
            Assert.Equal(new ulong[] { 0ul, 11ul, 15ul }, output);
            Assert.False(GpuTimestampResults.TryNormalize(new ulong[] { 0ul, ulong.MaxValue, 0ul }, 64, output));
            Assert.Equal(new ulong[] { 0ul, 11ul, 15ul }, output);

            Assert.True(GpuTimestampResults.TryNormalize(new ulong[] { ulong.MaxValue - 1, 1ul }, 64, output));
            Assert.Equal(new ulong[] { 0ul, 3ul, 15ul }, output);
            Assert.True(GpuTimestampResults.TryNormalize(new ulong[] { 250ul, 261ul, 521ul }, 8, output));
            Assert.Equal(new ulong[] { 0ul, 11ul, 15ul }, output);
            Assert.True(GpuTimestampResults.TryNormalize(new ulong[] { 0ul, 0ul, 0ul }, 1, output));
            Assert.Equal(new ulong[] { 0ul, 0ul, 0ul }, output);
            Assert.True(GpuTimestampResults.TryNormalize(new ulong[] { 17ul }, 64, output));
            Assert.Equal(new ulong[] { 0ul, 0ul, 0ul }, output);
        }

        [Theory]
        [InlineData(8, new ulong[] { 250ul, 5ul, 250ul, 5ul }, new ulong[] { 0ul, 11ul, 256ul, 267ul })]
        [InlineData(64, new ulong[] { 0ul, ulong.MaxValue }, new ulong[] { 0ul, ulong.MaxValue })]
        public void ElapsedTicksCanReachTheFullRange(int validBits, ulong[] raw, ulong[] expected)
        {
            var output = new ulong[raw.Length];
            Assert.True(GpuTimestampResults.TryNormalize(raw, validBits, output));
            Assert.Equal(expected, output);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(65)]
        [InlineData(-1)]
        public void InvalidCounterWidthsLeaveTheDestinationUntouched(int validBits)
        {
            ulong[] output = { 99ul, 98ul, 97ul };
            Assert.False(GpuTimestampResults.TryNormalize(new ulong[] { 250ul, 5ul, 9ul }, validBits, output));
            Assert.Equal(new ulong[] { 99ul, 98ul, 97ul }, output);
        }

        [Fact]
        public void MissingBaselineAndSmallDestinationsAreRefusedWithoutWrites()
        {
            ulong[] output = { 99ul, 98ul };
            Assert.False(GpuTimestampResults.TryNormalize(new ulong[] { 250ul, 5ul, 9ul }, 8, output));
            Assert.Equal(new ulong[] { 99ul, 98ul }, output);
            Assert.False(GpuTimestampResults.TryNormalize(ReadOnlySpan<ulong>.Empty, 8, output));
            Assert.Equal(new ulong[] { 99ul, 98ul }, output);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(0, 1)]
        [InlineData(1, 0)]
        public void NormalizationSupportsOverlappingSpans(int sourceStart, int destinationStart)
        {
            ulong[] storage = { 99ul, 99ul, 99ul, 99ul };
            new ulong[] { 250ul, 5ul, 9ul }.CopyTo(storage, sourceStart);
            Assert.True(GpuTimestampResults.TryNormalize(storage.AsSpan(sourceStart, 3), 8,
                storage.AsSpan(destinationStart, 3)));
            Assert.Equal(new ulong[] { 0ul, 11ul, 15ul }, storage.AsSpan(destinationStart, 3).ToArray());
        }

        [Fact]
        public void ExistingImplementersRefuseOptionalTimestampOperations()
        {
            IGpuResourceFactory factory = new FakeGpuResourceFactory();
            using IGpuCommandList commands = new NullGpuCommandList();
            Assert.Throws<NotSupportedException>(() => factory.CreateTimestampPool(4));
            Assert.Throws<NotSupportedException>(() => commands.BeginTimestamps(null!, 2));
            Assert.Throws<NotSupportedException>(() => commands.WriteTimestamp(null!, 0));
            Assert.Throws<NotSupportedException>(() => commands.EndTimestamps(null!));
        }
    }
}
