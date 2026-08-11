using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE QUEUED HALF OF M-W7: what a resize and a runtime vsync change do between two present boundaries.
    /// Work-breakdown row 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/581).
    ///
    /// <para><b>THE TWO ARE INDEPENDENT HERE AND ARE ONE FLAG ON THE VULKAN SIBLING, so the independence is what
    /// this pins.</b> There a resize and a present-mode change are the same event, because a swapchain can have
    /// neither changed in place and both end in a full recreate. Metal needs no recreation at all: a resize is a
    /// <c>drawableSize</c> write and a vsync change is a <c>displaySyncEnabled</c> write. A shared flag would make
    /// a plain vsync toggle rewrite the drawable size, which is a resize nobody asked for, and on a Retina window
    /// that is a visible resolution change.</para>
    /// </summary>
    public sealed class MetalPresentPendingTests
    {
        /// <summary>Nothing queued is nothing to do, which is what keeps an ordinary boundary from paying for a
        /// drain.</summary>
        [Fact]
        public void NothingIsQueuedOnAFreshOne()
        {
            var pending = new MetalPresentPending();

            Assert.False(pending.HasWork);
            Assert.Null(pending.PendingSize);
            Assert.Null(pending.PendingSyncToVerticalBlank);
            Assert.False(pending.Take(out _, out _));
        }

        /// <summary>
        /// A BURST OF SIZE EVENTS COSTS ONE APPLY, which is the whole reason a drag-resize is affordable. Thirty
        /// events between two boundaries leave the LAST one and nothing else.
        /// </summary>
        [Fact]
        public void AResizeCoalescesToTheLastRequest()
        {
            var pending = new MetalPresentPending();

            for (uint i = 1; i <= 30; i++) pending.QueueResize(i * 10u, i * 20u);

            Assert.True(pending.Take(out MetalDrawableSize? size, out bool? sync));
            Assert.Equal(new MetalDrawableSize(300u, 600u), size);
            Assert.Null(sync);
        }

        /// <summary>A vsync change coalesces the same way, and a redundant one is still queued: the write is
        /// unconditional and cheap (M-W2), so the incumbent's compare-against-our-own-field is not
        /// reproduced.</summary>
        [Fact]
        public void AVsyncChangeCoalescesToTheLastRequest()
        {
            var pending = new MetalPresentPending();

            pending.QueueSyncToVerticalBlank(true);
            pending.QueueSyncToVerticalBlank(false);
            pending.QueueSyncToVerticalBlank(true);

            Assert.True(pending.Take(out MetalDrawableSize? size, out bool? sync));
            Assert.Null(size);
            Assert.True(sync);
        }

        /// <summary>
        /// THE TWO DO NOT INTERFERE, in either order. A vsync toggle must not carry a size with it and a resize
        /// must not carry a vsync value, because the boundary applies exactly what it is handed and a null is what
        /// tells it not to write a property at all.
        /// </summary>
        [Fact]
        public void AResizeAndAVsyncChangeSurviveEachOther()
        {
            var pending = new MetalPresentPending();

            pending.QueueSyncToVerticalBlank(false);
            pending.QueueResize(640u, 480u);

            Assert.True(pending.HasWork);
            Assert.Equal(new MetalDrawableSize(640u, 480u), pending.PendingSize);
            Assert.False(pending.PendingSyncToVerticalBlank);

            Assert.True(pending.Take(out MetalDrawableSize? size, out bool? sync));
            Assert.Equal(new MetalDrawableSize(640u, 480u), size);
            Assert.False(sync);
        }

        /// <summary>
        /// A TAKE CLEARS EVERYTHING, so a request arriving while the boundary runs is queued for the NEXT boundary
        /// rather than applied twice. Applying a resize twice is harmless and applying a stale one over a newer
        /// one is not, which is why this is a clear rather than a peek.
        /// </summary>
        [Fact]
        public void ATakeClearsBothHalves()
        {
            var pending = new MetalPresentPending();

            pending.QueueResize(1u, 2u);
            pending.QueueSyncToVerticalBlank(true);

            Assert.True(pending.Take(out _, out _));
            Assert.False(pending.HasWork);
            Assert.False(pending.Take(out MetalDrawableSize? size, out bool? sync));
            Assert.Null(size);
            Assert.Null(sync);
        }

        /// <summary>
        /// A ZERO SIZE IS QUEUEABLE AND SURVIVES THE ROUND TRIP, which is the minimised window. The clamp belongs
        /// to the apply and not to the queue, so this must come back as the zero it was: a queue that clamped
        /// would make a minimised window indistinguishable from a one-by-one one, and only one of those recovers.
        /// </summary>
        [Fact]
        public void AZeroSizeSurvivesTheQueueUnclamped()
        {
            var pending = new MetalPresentPending();

            pending.QueueResize(0u, 0u);

            Assert.True(pending.Take(out MetalDrawableSize? size, out _));
            Assert.Equal(new MetalDrawableSize(0u, 0u), size);
            Assert.True(size!.Value.IsEmpty);
        }

        /// <summary>
        /// THE PACKING SURVIVES A SIZE WITH ITS HIGH BIT SET, which is the one thing a two-halves-in-one-long
        /// scheme can get wrong. A width whose top bit is set makes the packed value negative, and the sentinel
        /// for "nothing queued" is also negative, so this is the case where a naive check would read a real
        /// request as an empty queue.
        /// </summary>
        [Fact]
        public void ThePackingSurvivesAWidthWithItsHighBitSet()
        {
            var pending = new MetalPresentPending();

            pending.QueueResize(0x8000_0000u, 7u);

            Assert.True(pending.HasWork);
            Assert.True(pending.Take(out MetalDrawableSize? size, out _));
            Assert.Equal(new MetalDrawableSize(0x8000_0000u, 7u), size);
        }
    }
}
