using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render2D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    /// <summary>
    /// Guards SpriteBatch submission-order batching: quads must be grouped into runs that preserve the
    /// order they were drawn, coalescing only *consecutive* same-texture draws. The old behaviour grouped
    /// globally per texture, which let a later draw jump ahead of an earlier draw using a different texture
    /// (e.g. menu text painting on top of a modal panel drawn after it). Also guards the opt-in
    /// texture-grouping helper (<see cref="QuadRunBuilder{T}.GroupKeysInFirstSeenOrder"/>) used by
    /// <c>SpriteBatch.GroupByTexture</c>.
    /// </summary>
    public class SpriteBatchOrderTests
    {
        static readonly object TexA = new();
        static readonly object TexB = new();
        static readonly object TexC = new();

        sealed class BatchHarness : IDisposable
        {
            readonly FakeGpuDevice _device = new();
            readonly Render2DCore _core;

            public BatchHarness()
            {
                var output = new GpuOutputDescription(null, new[] { GpuPixelFormat.R8G8B8A8UNorm });
                _core = new Render2DCore(_device, output, ownsDevice: false);
                Commands = new RecordingGpuCommandList(new NullGpuCommandList());
                Batch = _core.Batch;
                Batch.NewFrame(Commands, 64, 64);
                A = _core.CreateTexture(new byte[] { 255, 0, 0, 255 }, 1, 1);
                B = _core.CreateTexture(new byte[] { 0, 0, 255, 255 }, 1, 1);
            }

            public SpriteBatch Batch { get; }
            public RecordingGpuCommandList Commands { get; }
            public Texture2D A { get; }
            public Texture2D B { get; }

            public void Dispose()
            {
                A.Dispose();
                B.Dispose();
                Commands.Dispose();
                _core.Dispose();
                _device.Dispose();
            }
        }

        // A run's items, read back through the (Start, Count) slice into Items - the shape every real
        // consumer (SpriteBatch.Flush) reads through, now that runs no longer carry their own List<T>.
        static T[] ItemsOf<T>(QuadRunBuilder<T> b, int runIndex)
        {
            var (_, start, count) = b.Runs[runIndex];
            return b.Items.Skip(start).Take(count).ToArray();
        }

        [Fact]
        public void ConsecutiveSameKeyCoalescesIntoOneRun()
        {
            var b = new QuadRunBuilder<int>();
            b.Add(TexA, 1);
            b.Add(TexA, 2);

            Assert.Single(b.Runs);
            Assert.Same(TexA, b.Runs[0].Key);
            Assert.Equal(new[] { 1, 2 }, ItemsOf(b, 0));
        }

        [Fact]
        public void InterleavedTexturesProduceOrderedRunsNotGlobalGroups()
        {
            var b = new QuadRunBuilder<int>();
            b.Add(TexA, 1);   // panel-under
            b.Add(TexB, 2);   // text-over
            b.Add(TexA, 3);   // panel-on-top: must stay AFTER the text, not merge with the first A

            Assert.Equal(3, b.Runs.Count);
            Assert.Same(TexA, b.Runs[0].Key);
            Assert.Same(TexB, b.Runs[1].Key);
            Assert.Same(TexA, b.Runs[2].Key);
            Assert.Equal(new[] { 1 }, ItemsOf(b, 0));
            Assert.Equal(new[] { 2 }, ItemsOf(b, 1));
            Assert.Equal(new[] { 3 }, ItemsOf(b, 2));
        }

        [Fact]
        public void ResetClearsRuns()
        {
            var b = new QuadRunBuilder<int>();
            b.Add(TexA, 1);
            b.Reset();

            Assert.Empty(b.Runs);
            Assert.Empty(b.Items);
        }

        [Fact]
        public void ResetReusesBackingCapacity_NoNewAllocationNeededToRegrow()
        {
            // Not a strict no-alloc assertion (xunit has no built-in allocation gate), but proves the backing
            // list survives Reset() with its capacity intact: re-adding the same count of items after Reset
            // must not need the list to grow again (Capacity does not increase on the second build).
            var b = new QuadRunBuilder<int>();
            for (int i = 0; i < 64; i++) b.Add(TexA, i);
            int capacityAfterFirstBuild = ((List<int>)b.Items).Capacity;

            b.Reset();
            for (int i = 0; i < 64; i++) b.Add(TexA, i);

            Assert.Equal(capacityAfterFirstBuild, ((List<int>)b.Items).Capacity);
            Assert.Equal(64, b.Items.Count);
        }

        [Fact]
        public void GroupKeysInFirstSeenOrder_MergesNonConsecutiveRunsPerKey()
        {
            var b = new QuadRunBuilder<int>();
            b.Add(TexA, 1);
            b.Add(TexB, 2);
            b.Add(TexA, 3);
            b.Add(TexC, 4);
            b.Add(TexB, 5);

            var keys = b.GroupKeysInFirstSeenOrder();

            // First-seen order: A (run 0), B (run 1), C (run 3).
            Assert.Equal(new object[] { TexA, TexB, TexC }, keys);

            // A's group is runs {0, 2} (items 1 and 3), in submission order.
            var aRuns = b.RunIndicesForGroup(TexA);
            Assert.Equal(new[] { 0, 2 }, aRuns);
            Assert.Equal(new[] { 1, 3 }, aRuns.SelectMany(idx => ItemsOf(b, idx)));

            // B's group is runs {1, 4} (items 2 and 5), in submission order.
            var bRuns = b.RunIndicesForGroup(TexB);
            Assert.Equal(new[] { 1, 4 }, bRuns);
            Assert.Equal(new[] { 2, 5 }, bRuns.SelectMany(idx => ItemsOf(b, idx)));

            // C's group is just run {3} (item 4).
            var cRuns = b.RunIndicesForGroup(TexC);
            Assert.Equal(new[] { 3 }, cRuns);
            Assert.Equal(new[] { 4 }, cRuns.SelectMany(idx => ItemsOf(b, idx)));
        }

        [Fact]
        public void GroupKeysInFirstSeenOrder_AllSameKeyIsOneGroupOneRun()
        {
            var b = new QuadRunBuilder<int>();
            b.Add(TexA, 1);
            b.Add(TexA, 2);
            b.Add(TexA, 3);

            var keys = b.GroupKeysInFirstSeenOrder();

            Assert.Equal(new object[] { TexA }, keys);
            Assert.Equal(new[] { 0 }, b.RunIndicesForGroup(TexA));
        }

        [Fact]
        public void GroupKeysInFirstSeenOrder_ReusedAcrossBuilds_ReflectsOnlyLatestBuild()
        {
            var b = new QuadRunBuilder<int>();
            b.Add(TexA, 1);
            b.Add(TexB, 2);
            b.GroupKeysInFirstSeenOrder();

            b.Reset();
            b.Add(TexC, 9);
            var keys = b.GroupKeysInFirstSeenOrder();

            // Stale groups from the prior build (A, B) must not leak into the new build's key list.
            Assert.Equal(new object[] { TexC }, keys);
            Assert.Equal(new[] { 0 }, b.RunIndicesForGroup(TexC));
        }

        [Fact]
        public void PublicFlushScopesGroupingAndLeavesBatchOpen()
        {
            using var h = new BatchHarness();
            h.Batch.Begin();
            h.Batch.GroupByTexture = true;
            h.Batch.Draw(h.A, new Vector2(0, 0), Color.White);
            h.Batch.Draw(h.B, new Vector2(1, 0), Color.White);
            h.Batch.Draw(h.A, new Vector2(2, 0), Color.White);

            h.Batch.Flush();

            Assert.Equal(2, h.Commands.DrawCount);
            Assert.Equal(1, h.Batch.FrameStats.Flushes);

            h.Batch.GroupByTexture = false;
            h.Batch.Draw(h.A, new Vector2(0, 1), Color.White);
            h.Batch.Draw(h.B, new Vector2(1, 1), Color.White);
            h.Batch.Draw(h.A, new Vector2(2, 1), Color.White);
            h.Batch.End();

            Assert.Equal(5, h.Commands.DrawCount);
            Assert.Equal(2, h.Batch.FrameStats.Flushes);
        }

        [Fact]
        public void EmptyAndRepeatedFlushesKeepTheActiveBatchReusable()
        {
            using var h = new BatchHarness();
            h.Batch.Begin();

            h.Batch.Flush();
            h.Batch.Flush();
            Assert.Equal(0, h.Batch.FrameStats.Flushes);

            h.Batch.Draw(h.A, Vector2.Zero, Color.White);
            h.Batch.Flush();
            h.Batch.Flush();
            Assert.Equal(1, h.Batch.FrameStats.Flushes);

            h.Batch.Draw(h.B, Vector2.One, Color.White);
            h.Batch.End();
            Assert.Equal(2, h.Batch.FrameStats.Flushes);
        }

        [Fact]
        public void FlushRequiresAnActiveBatch()
        {
            using var h = new BatchHarness();

            Assert.Throws<InvalidOperationException>(() => h.Batch.Flush());
            h.Batch.Begin();
            h.Batch.End();
            Assert.Throws<InvalidOperationException>(() => h.Batch.Flush());
        }
    }
}
