using System;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The device-free half of the native Direct3D 11 event-query fallback (decision C5): turning a pile of
    /// one-shot event queries into the single monotonic counter the fence subsystem is built on.
    /// <para>
    /// This half is the one that can be wrong. What is left in <c>D3D11EventQueryTimeline</c> is four native
    /// calls, and every decision around them (which marker to poll, when the counter advances, when a query is
    /// reusable, what disposal owns) is here where a plain <c>[Fact]</c> reaches it on any operating system.
    /// </para>
    /// <para>
    /// Worth testing at all because the fallback is the path NOBODY will exercise. It runs only on Windows older
    /// than 10 1703, so no CI leg and no development machine takes it, and the first machine that does is a
    /// player's. A silent defect there presents as a resource pool that never frees, which reads as a memory leak
    /// somewhere else entirely.
    /// </para>
    /// </summary>
    public sealed class D3D11EventTimelineQueueTests
    {
        [Fact]
        public void AFreshQueue_HasIssuedNothingAndCompletedNothing()
        {
            var queue = new D3D11EventTimelineQueue();

            Assert.Equal(0UL, queue.Issued);
            Assert.Equal(0UL, queue.Completed);
            Assert.Equal(0, queue.PendingCount);
            Assert.False(queue.TryPeekOldest(out _));
        }

        /// <summary>
        /// Values start at 1 and increase by one. The subsystem leans on 0 being unreachable: it is the unarmed
        /// marker on <c>D3D11GpuFence</c>, so a value of 0 must never be something a real signal can produce.
        /// </summary>
        [Fact]
        public void IssuedValues_StartAtOneAndIncreaseByOne()
        {
            var queue = new D3D11EventTimelineQueue();

            Assert.Equal(1UL, queue.Enqueue(new object()));
            Assert.Equal(2UL, queue.Enqueue(new object()));
            Assert.Equal(3UL, queue.Enqueue(new object()));
            Assert.Equal(3UL, queue.Issued);
        }

        /// <summary>
        /// THE ORDERING RULE. Markers are placed on the immediate context, which consumes work in submission
        /// order, so the oldest is the only one worth asking about and retiring it advances the counter to its
        /// value. A queue that polled the NEWEST marker would report everything complete the moment the last
        /// submission finished, which is true in the common case and wrong in exactly the case a fence exists
        /// for.
        /// </summary>
        [Fact]
        public void RetiringInIssueOrder_AdvancesTheCompletedValueOneSignalAtATime()
        {
            var queue = new D3D11EventTimelineQueue();
            object first = new();
            object second = new();
            queue.Enqueue(first);
            queue.Enqueue(second);

            Assert.True(queue.TryPeekOldest(out object oldest));
            Assert.Same(first, oldest);

            queue.RetireOldest();
            Assert.Equal(1UL, queue.Completed);
            Assert.True(queue.TryPeekOldest(out oldest));
            Assert.Same(second, oldest);

            queue.RetireOldest();
            Assert.Equal(2UL, queue.Completed);
            Assert.False(queue.TryPeekOldest(out _));
        }

        /// <summary>A retired marker is immediately available again, which is what bounds the query pool at the
        /// number of submissions in flight rather than letting it grow one object per frame forever.</summary>
        [Fact]
        public void ARetiredMarker_IsRentedBackInsteadOfANewOneBeingNeeded()
        {
            var queue = new D3D11EventTimelineQueue();
            object marker = new();
            queue.Enqueue(marker);

            Assert.Null(queue.Rent());          // nothing retired yet, the caller has to create one

            queue.RetireOldest();

            Assert.Equal(1, queue.RecycledCount);
            Assert.Same(marker, queue.Rent());
            Assert.Equal(0, queue.RecycledCount);
        }

        /// <summary>
        /// The steady state the recycling is for: signal, retire, signal, retire, on one object. Asserted as a
        /// count rather than trusted, because "it reuses them" is invisible from outside and an allocation per
        /// frame is exactly the kind of cost that is never noticed until a soak session.
        /// </summary>
        [Fact]
        public void ASteadyStreamOfSignals_KeepsReusingTheSameMarker()
        {
            var queue = new D3D11EventTimelineQueue();
            object marker = new();

            for (int i = 0; i < 100; i++)
            {
                object rented = queue.Rent() ?? marker;
                Assert.Same(marker, rented);
                queue.Enqueue(rented);
                queue.RetireOldest();
            }

            Assert.Equal(100UL, queue.Completed);
            Assert.Equal(0, queue.PendingCount);
            Assert.Equal(1, queue.RecycledCount);
        }

        /// <summary>
        /// Retiring with nothing in flight is a defect in the caller's poll loop and is loud, because the quiet
        /// version of it runs the completed value ahead of the GPU. Everything downstream of this counter reads
        /// it as proof that work has finished.
        /// </summary>
        [Fact]
        public void RetiringWithNothingInFlight_ThrowsRatherThanRunningTheCounterAhead()
        {
            var queue = new D3D11EventTimelineQueue();

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(queue.RetireOldest);
            Assert.Contains("nothing in flight", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void EnqueueingNothing_Throws()
            => Assert.Throws<ArgumentNullException>(() => new D3D11EventTimelineQueue().Enqueue(null!));

        /// <summary>
        /// Disposal hands back everything the queue owns, in flight and retired alike, because the caller is the
        /// only one that can release the native objects. A queue that handed back only the free list would leak
        /// exactly the queries a session died holding.
        /// </summary>
        [Fact]
        public void TakeEveryMarker_ReturnsTheInFlightOnesAndTheRecycledOnes()
        {
            var queue = new D3D11EventTimelineQueue();
            object retired = new();
            object inFlight = new();
            queue.Enqueue(retired);
            queue.RetireOldest();
            queue.Enqueue(inFlight);

            object[] all = queue.TakeEveryMarker();

            Assert.Equal(2, all.Length);
            Assert.Contains(retired, all);
            Assert.Contains(inFlight, all);
            Assert.Equal(0, queue.PendingCount);
            Assert.Equal(0, queue.RecycledCount);
        }

        /// <summary>The counters survive disposal, so a value read at teardown still reports what it reported
        /// before rather than resetting to zero and reading as "nothing ever completed".</summary>
        [Fact]
        public void TakeEveryMarker_LeavesTheCountersWhereTheyWere()
        {
            var queue = new D3D11EventTimelineQueue();
            queue.Enqueue(new object());
            queue.RetireOldest();
            queue.Enqueue(new object());

            queue.TakeEveryMarker();

            Assert.Equal(2UL, queue.Issued);
            Assert.Equal(1UL, queue.Completed);
        }
    }
}
