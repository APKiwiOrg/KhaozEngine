using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE RECORDING CONTRACT (V-R4), device-free: N lists open CONCURRENTLY on N threads, sealed in an
    /// interleaved order, and submitted in an order that is neither the order they were begun in nor the order
    /// they were sealed in. What must survive all of that is per-list ORDER and the concatenation being SUBMIT
    /// order.
    ///
    /// <para><b>THIS MIRRORS <c>D3D11RecorderContractTests</c> AT THE SEAM LEVEL AND MEASURES SOMETHING
    /// DIFFERENT.</b> There, two lists over one immediate context can corrupt each other through shared device
    /// state, and the test's job is to demonstrate that corruption and the shape that prevents it. Here there is
    /// no shared state to corrupt: a <c>VkCommandPool</c> and its buffers are externally synchronised one thread
    /// at a time, and per-list pools mean two lists on two threads never touch the same pool. So what this file
    /// asserts is that the property actually HOLDS in the shipped types, rather than that it was intended: each
    /// list's pool sees exactly its own reset, begin and end, in that order, with nothing from another list
    /// interleaved into it, and the timeline values come out in submit order.</para>
    ///
    /// <para><b>THE OBSERVABLE IS THE SUBMIT PATH AND THE SLOT BOOKKEEPING, because recording content does not
    /// exist yet.</b> Rows 11, 12, 14 and 15 build the members that would put draws into these buffers, and until
    /// they do, a "record" is the three native calls a <c>Begin</c> and an <c>End</c> make. Row 11
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/521) EXTENDS this file rather than replacing it: the same
    /// N-list interleaving, driven through a <see cref="VulkanCountingCmdSink"/> per list, asserting that each
    /// list's binds and draws appear in its own buffer in record order and in no other buffer at all. The
    /// structure below is written to take that extension without moving: the per-pool trace is already the
    /// assertion unit.</para>
    ///
    /// <para><b>WHAT REMAINS UNTESTABLE HERE</b> is that a driver honours the ordering, which needs a live queue
    /// and belongs to the <c>vulkan-native</c> CI leg (row 19).</para>
    /// </summary>
    public sealed class VulkanRecordingContractTests
    {
        const int Lists = 4;

        /// <summary>
        /// FOUR LISTS BEGIN CONCURRENTLY ON FOUR THREADS, seal in reverse order, and submit in a third order.
        /// Every list's own pool trace is exactly its own three calls in order, and no list's pool sees another
        /// list's calls. That is the whole of "N lists record concurrently and genuinely".
        /// </summary>
        [Fact]
        public void NListsRecordingConcurrently_KeepTheirOwnPoolTracesIntact()
        {
            using var fixture = new VulkanCommandListTests.Fixture(depth: 3);
            VulkanCommandList[] lists = CreateLists(fixture);

            // The barrier releases AFTER Begin returns, not while a thread is still inside it, so what this
            // establishes is all four lists in the OPEN-RECORDING state at the SAME TIME, which is the state the
            // seam's portable contract forbids and this backend permits. Dedicated threads rather than
            // Parallel.For, because a barrier needs every participant actually running at once and the pool is
            // free to schedule fewer.
            using var allBegun = new Barrier(Lists);
            var threads = new Thread[Lists];
            for (int i = 0; i < Lists; i++)
            {
                int index = i;
                threads[i] = new Thread(() =>
                {
                    lists[index].Begin();
                    allBegun.SignalAndWait();
                });
                threads[i].Start();
            }

            foreach (Thread thread in threads) thread.Join();

            // Sealed in REVERSE order, so seal order is not begin order.
            for (int i = Lists - 1; i >= 0; i--) lists[i].End();

            // And submitted in a third order, so submit order is neither.
            int[] submitOrder = { 2, 0, 3, 1 };
            var values = new ulong[Lists];
            foreach (int i in submitOrder) values[i] = fixture.Submits.Submit(lists[i], null);

            for (int i = 0; i < Lists; i++)
            {
                ulong pool = lists[i].Ring.Pools[0];
                int p = fixture.Api.IndexOf(pool);

                Assert.Equal(
                    new[]
                    {
                        $"CreatePool -> p{p}", $"AllocateBuffer(p{p}) -> b{p}", $"ResetPool(p{p})",
                        $"Begin(b{p})", $"End(b{p})", $"Submit(b{p},{values[i]})",
                    },
                    fixture.Api.EventsForPool(pool));
            }

            foreach (VulkanCommandList list in lists) list.Dispose();
        }

        /// <summary>
        /// THE VALUES COME OUT IN SUBMIT ORDER AND NOWHERE ELSE. Record order and seal order are both different
        /// from submit order above, and the timeline is what the seam's fence contract is written against, so a
        /// value that followed record order would make a fence on the first-submitted list read signalled over
        /// work behind it in the queue.
        /// </summary>
        [Fact]
        public void TimelineValues_FollowSubmitOrderAndNotRecordOrder()
        {
            using var fixture = new VulkanCommandListTests.Fixture(depth: 3);
            VulkanCommandList[] lists = CreateLists(fixture);

            foreach (VulkanCommandList list in lists)
            {
                list.Begin();
                list.End();
            }

            int[] submitOrder = { 3, 1, 2, 0 };
            var values = new List<ulong>();
            foreach (int i in submitOrder) values.Add(fixture.Submits.Submit(lists[i], null));

            // Strictly increasing in the order the submits were made, which is what the queue signals in.
            Assert.Equal(new ulong[] { 1, 2, 3, 4 }, values);

            // And each list's slot carries the value ITS submit took, not the value of the record before it.
            for (int position = 0; position < submitOrder.Length; position++)
            {
                Assert.Equal(values[position], lists[submitOrder[position]].Ring.SubmittedAt(0));
            }

            foreach (VulkanCommandList list in lists) list.Dispose();
        }

        /// <summary>
        /// THE SUBMIT LOCK MAKES ALLOCATION ORDER AND SUBMIT ORDER THE SAME ORDER, which is the precondition
        /// <c>NextSubmitValue</c> states. Allocating outside the lock would let two threads take 5 and 6 and then
        /// reach vkQueueSubmit in the other order, asking the queue to signal 6 and then 5, which violates the
        /// strictly-increasing rule the whole one-timeline theorem rests on. Driven from many threads at once,
        /// because a single-threaded test cannot see this property at all.
        /// </summary>
        [Fact]
        public void UnderConcurrentSubmission_TheValueOrderIsTheSubmitOrder()
        {
            const int threads = 8;
            const int perThread = 40;

            using var fixture = new VulkanCommandListTests.Fixture(depth: 3);
            var lists = new VulkanCommandList[threads];
            for (int i = 0; i < threads; i++) lists[i] = fixture.CreateList();

            Parallel.For(0, threads, t =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    lists[t].Begin();
                    lists[t].End();
                    fixture.Submits.Submit(lists[t], null);
                }
            });

            // The fake records (buffer, value) in the order the submits reached it, so this list IS submit order.
            ulong[] inSubmitOrder = fixture.Api.Submissions.Select(s => s.Value).ToArray();

            Assert.Equal(threads * perThread, inSubmitOrder.Length);
            for (int i = 0; i < inSubmitOrder.Length; i++) Assert.Equal((ulong)(i + 1), inSubmitOrder[i]);
            Assert.Equal((ulong)(threads * perThread), fixture.Timeline.LastSubmitted);

            foreach (VulkanCommandList list in lists) list.Dispose();
        }

        /// <summary>
        /// THE SLOT REUSE DISCIPLINE ACROSS N LISTS: each list walks its OWN slots and wraps onto its own oldest
        /// one, waiting for that slot's value rather than the device's newest. A ring that waited for the newest
        /// would serialise every list behind whichever one submitted last, which at four lists is three
        /// unnecessary drains per frame.
        /// </summary>
        [Fact]
        public void EachListWrapsOntoItsOwnOldestSlot_AndWaitsForThatSlotsValue()
        {
            using var fixture = new VulkanCommandListTests.Fixture(depth: 2);
            VulkanCommandList[] lists = CreateLists(fixture);

            // Round one and round two fill both slots of every list, interleaved across the lists so that the
            // device's newest value is never the one any single list is about to wait for.
            var firstValueOf = new ulong[Lists];
            for (int round = 0; round < 2; round++)
            {
                for (int i = 0; i < Lists; i++)
                {
                    ulong value = fixture.RecordAndSubmit(lists[i]);
                    if (round == 0) firstValueOf[i] = value;
                }
            }

            // Round three wraps every list onto its own slot 0, which is still in flight.
            for (int i = 0; i < Lists; i++)
            {
                lists[i].Begin();
                lists[i].End();

                Assert.Equal(firstValueOf[i], fixture.Semaphore.LastWaitValue);
            }

            Assert.Equal(Lists, fixture.Semaphore.WaitCount);
            Assert.Equal(Lists, fixture.Backpressure.Totals.Count);

            foreach (VulkanCommandList list in lists) list.Dispose();
        }

        static VulkanCommandList[] CreateLists(VulkanCommandListTests.Fixture fixture)
        {
            var lists = new VulkanCommandList[Lists];
            for (int i = 0; i < Lists; i++) lists[i] = fixture.CreateList();
            return lists;
        }
    }
}
