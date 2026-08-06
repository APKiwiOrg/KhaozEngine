using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Decision V-M2's suballocation arithmetic: first-fit over a sorted free list with alignment correction,
    /// split on allocate, merge with BOTH neighbours on free.
    /// <para>
    /// THIS IS THE FILE THE VMA DECLINE IS ARGUED AGAINST. Section 9.1 declines VMA and writes down the
    /// counterargument owed: hand-rolled allocators are where memory corruption lives, and the corruption in
    /// question is two live suballocations overlapping. That is an arithmetic property of one device-free type, so
    /// it is asserted here directly, case by case and then under randomized churn, on a machine with no Vulkan
    /// loader. What these rows cannot see is an ALIASING defect on a real GPU, which is why the decline is written
    /// as conditional on row 19's synchronisation-validation gate
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/529) existing.
    /// </para>
    /// </summary>
    public sealed class VulkanMemoryFreeListTests
    {
        /// <summary>A fresh list is one free range covering the whole chunk, and the first allocation comes off
        /// its start.</summary>
        [Fact]
        public void AFreshList_IsOneRangeAndAllocatesFromZero()
        {
            var list = new VulkanMemoryFreeList(1024);

            Assert.Equal(1024ul, list.Capacity);
            Assert.Equal(1024ul, list.FreeBytes);
            Assert.Equal(1, list.FreeBlockCount);
            Assert.True(list.IsEmpty);

            Assert.True(list.TryAllocate(256, 1, out ulong offset));
            Assert.Equal(0ul, offset);
            Assert.Equal(256ul, list.UsedBytes);
            Assert.Equal(768ul, list.FreeBytes);
            Assert.Equal(1, list.FreeBlockCount);
            Assert.False(list.IsEmpty);
        }

        /// <summary>An allocation splits the range it came out of, so the remainder stays allocatable and the
        /// next request takes the very next byte.</summary>
        [Fact]
        public void AllocatingSplitsTheRange_AndTheRemainderStaysUsable()
        {
            var list = new VulkanMemoryFreeList(1024);

            Assert.True(list.TryAllocate(300, 1, out ulong first));
            Assert.True(list.TryAllocate(300, 1, out ulong second));
            Assert.True(list.TryAllocate(424, 1, out ulong third));

            Assert.Equal(0ul, first);
            Assert.Equal(300ul, second);
            Assert.Equal(600ul, third);
            Assert.Equal(0ul, list.FreeBytes);
            Assert.Equal(0, list.FreeBlockCount);

            Assert.False(list.TryAllocate(1, 1, out _));
        }

        /// <summary>
        /// THE ALIGNMENT-PADDING SURFACE, and the single easiest thing in this type to get wrong. When alignment
        /// pushes an offset forward inside a free range, the bytes BEFORE the aligned offset go back on the free
        /// list as their own range rather than being absorbed into the allocation or dropped. Absorbing them would
        /// make a free return fewer bytes than were taken and leak a little on every aligned allocation, which
        /// over a long session is a slow bleed that reads as a driver leak.
        /// </summary>
        [Fact]
        public void AlignmentPadding_StaysAllocatableRatherThanBeingLost()
        {
            var list = new VulkanMemoryFreeList(1024);

            // Leaves the free list starting at 8, so a 256-aligned request has to skip 248 bytes.
            Assert.True(list.TryAllocate(8, 1, out ulong head));
            Assert.Equal(0ul, head);

            Assert.True(list.TryAllocate(64, 256, out ulong aligned));
            Assert.Equal(256ul, aligned);

            // The 248 bytes between them are STILL FREE and still countable.
            Assert.Equal(1024ul - 8 - 64, list.FreeBytes);
            Assert.Equal(2, list.FreeBlockCount);

            // And still allocatable, which is the half a byte count alone would not prove.
            Assert.True(list.TryAllocate(248, 1, out ulong padding));
            Assert.Equal(8ul, padding);
            Assert.Equal(1024ul - 8 - 64 - 248, list.FreeBytes);

            // Full reclamation: everything goes back and coalesces into one range again.
            list.Free(head);
            list.Free(aligned);
            list.Free(padding);
            Assert.Equal(1024ul, list.FreeBytes);
            Assert.Equal(1, list.FreeBlockCount);
        }

        /// <summary>Every offset handed out satisfies the alignment that was asked for, which is the property the
        /// whole correction exists for and the one a bind call would fail on.</summary>
        [Theory]
        [InlineData(1ul)]
        [InlineData(4ul)]
        [InlineData(16ul)]
        [InlineData(64ul)]
        [InlineData(256ul)]
        [InlineData(1024ul)]
        public void EveryOffset_SatisfiesTheRequestedAlignment(ulong alignment)
        {
            var list = new VulkanMemoryFreeList(1 << 20);

            // A deliberately awkward run: each allocation leaves the next one a badly aligned starting point.
            for (int i = 0; i < 32; i++)
            {
                Assert.True(list.TryAllocate(1, 1, out _));
                Assert.True(list.TryAllocate(17, alignment, out ulong offset));
                Assert.Equal(0ul, offset % alignment);
            }
        }

        /// <summary>First fit means the FIRST range that can hold the request once aligned, not the best one. A
        /// hole reopened early is reused before untouched space further along.</summary>
        [Fact]
        public void FirstFit_TakesTheEarliestRangeThatCanHoldIt()
        {
            var list = new VulkanMemoryFreeList(1024);

            Assert.True(list.TryAllocate(100, 1, out ulong a));
            Assert.True(list.TryAllocate(100, 1, out _));
            Assert.True(list.TryAllocate(100, 1, out _));

            list.Free(a);

            // 80 fits in the hole at 0, so it goes there rather than at the tail.
            Assert.True(list.TryAllocate(80, 1, out ulong reused));
            Assert.Equal(0ul, reused);

            // 200 does not fit in the 20 bytes left of that hole, so it skips to the tail.
            Assert.True(list.TryAllocate(200, 1, out ulong tail));
            Assert.Equal(300ul, tail);
        }

        /// <summary>
        /// FREEING MERGES WITH BOTH NEIGHBOURS. A middle allocation given back between two free ranges leaves ONE
        /// range, not three. Failing to merge on either side is what turns a load-unload cycle into a free list of
        /// unusable slivers, and the failure is invisible in a byte count: the totals are right and nothing large
        /// fits any more.
        /// </summary>
        [Fact]
        public void FreeingBetweenTwoFreeNeighbours_MergesIntoOneRange()
        {
            var list = new VulkanMemoryFreeList(900);

            Assert.True(list.TryAllocate(300, 1, out ulong first));
            Assert.True(list.TryAllocate(300, 1, out ulong middle));
            Assert.True(list.TryAllocate(300, 1, out ulong last));

            list.Free(first);
            list.Free(last);
            Assert.Equal(2, list.FreeBlockCount);

            list.Free(middle);
            Assert.Equal(1, list.FreeBlockCount);
            Assert.Equal(900ul, list.FreeBytes);
            Assert.Equal(900ul, list.LargestFreeBlock);

            // The proof that a merge happened rather than three ranges adding up: the whole chunk fits again.
            Assert.True(list.TryAllocate(900, 1, out ulong whole));
            Assert.Equal(0ul, whole);
        }

        /// <summary>Merging with only the LEFT neighbour, which is the case an insert that only ever looks
        /// forward would miss.</summary>
        [Fact]
        public void FreeingAfterAFreeNeighbour_MergesLeft()
        {
            var list = new VulkanMemoryFreeList(600);

            Assert.True(list.TryAllocate(200, 1, out ulong first));
            Assert.True(list.TryAllocate(200, 1, out ulong second));
            Assert.True(list.TryAllocate(200, 1, out _));

            list.Free(first);
            list.Free(second);

            Assert.Equal(1, list.FreeBlockCount);
            Assert.Equal(400ul, list.LargestFreeBlock);
        }

        /// <summary>Merging with only the RIGHT neighbour, the mirror of the row above.</summary>
        [Fact]
        public void FreeingBeforeAFreeNeighbour_MergesRight()
        {
            var list = new VulkanMemoryFreeList(600);

            Assert.True(list.TryAllocate(200, 1, out _));
            Assert.True(list.TryAllocate(200, 1, out ulong second));
            Assert.True(list.TryAllocate(200, 1, out ulong third));

            list.Free(third);
            list.Free(second);

            Assert.Equal(1, list.FreeBlockCount);
            Assert.Equal(400ul, list.LargestFreeBlock);
        }

        /// <summary>EXHAUSTION IS FALSE, NOT A THROW. A full chunk is the ordinary state of a busy pool, and false
        /// is what tells the pool to try the next chunk or create one.</summary>
        [Fact]
        public void AFullList_AnswersFalseRatherThanThrowing()
        {
            var list = new VulkanMemoryFreeList(512);

            Assert.True(list.TryAllocate(512, 1, out _));
            Assert.False(list.TryAllocate(1, 1, out ulong offset));
            Assert.Equal(0ul, offset);
        }

        /// <summary>A request larger than the whole chunk is the same answer, which is what routes it to the
        /// allocator's dedicated path rather than to an exception.</summary>
        [Fact]
        public void ARequestLargerThanTheChunk_AnswersFalse()
        {
            var list = new VulkanMemoryFreeList(512);

            Assert.False(list.TryAllocate(513, 1, out _));
            Assert.Equal(512ul, list.FreeBytes);
            Assert.Equal(1, list.FreeBlockCount);
        }

        /// <summary>An alignment that pushes past the end of the only range is a miss rather than an offset
        /// outside the chunk, which is the arithmetic a bind call would silently accept and the GPU would
        /// not.</summary>
        [Fact]
        public void AnAlignmentThatPushesPastTheEnd_AnswersFalse()
        {
            var list = new VulkanMemoryFreeList(1024);

            Assert.True(list.TryAllocate(600, 1, out _));

            // The remaining range is [600, 1024). Aligning to 1024 lands at 1024, which is the end.
            Assert.False(list.TryAllocate(1, 1024, out _));
        }

        /// <summary>THE ZERO-SIZE GUARD. A zero-byte suballocation has no offset that means anything, two of them
        /// would share one, and a free could not tell them apart.</summary>
        [Fact]
        public void AZeroSizeRequest_Throws()
        {
            var list = new VulkanMemoryFreeList(1024);

            Assert.Throws<ArgumentOutOfRangeException>(() => list.TryAllocate(0, 1, out _));
        }

        /// <summary>An alignment that is zero or not a power of two did not come off a
        /// <c>VkMemoryRequirements</c>, so it is refused rather than rounded into something plausible.</summary>
        [Theory]
        [InlineData(0ul)]
        [InlineData(3ul)]
        [InlineData(24ul)]
        [InlineData(1000ul)]
        public void ANonPowerOfTwoAlignment_Throws(ulong alignment)
        {
            var list = new VulkanMemoryFreeList(1024);

            Assert.Throws<ArgumentOutOfRangeException>(() => list.TryAllocate(16, alignment, out _));
        }

        /// <summary>A chunk of zero bytes could satisfy nothing, and <c>vkAllocateMemory</c> rejects an
        /// <c>allocationSize</c> of 0 outright.</summary>
        [Fact]
        public void AZeroCapacityList_Throws()
            => Assert.Throws<ArgumentOutOfRangeException>(() => new VulkanMemoryFreeList(0));

        /// <summary>
        /// FREEING AN OFFSET NOTHING OWNS THROWS. This type is engine-internal with no consumer-reachable path to
        /// it, so a bad offset is a bug in this package rather than bad input. Trusting it and merging a range
        /// nothing owns corrupts the list into an overlap that surfaces later, somewhere unrelated.
        /// </summary>
        [Fact]
        public void FreeingAnUnknownOffset_Throws()
        {
            var list = new VulkanMemoryFreeList(1024);

            Assert.True(list.TryAllocate(64, 1, out ulong offset));

            Assert.Throws<InvalidOperationException>(() => list.Free(offset + 1));
            Assert.Throws<InvalidOperationException>(() => list.Free(512));
            Assert.Throws<InvalidOperationException>(() => list.Free(0xdeadbeef));

            // And the list is untouched by the refusals.
            Assert.Equal(64ul, list.UsedBytes);
            Assert.Equal(1, list.LiveCount);
        }

        /// <summary>Freeing the same offset twice is the same misuse and throws the same way. It is the shape a
        /// double dispose produces, and quietly merging the range a second time would double-count the bytes and
        /// then hand the same range out twice.</summary>
        [Fact]
        public void FreeingTwice_Throws()
        {
            var list = new VulkanMemoryFreeList(1024);

            Assert.True(list.TryAllocate(64, 1, out ulong offset));
            list.Free(offset);

            Assert.Throws<InvalidOperationException>(() => list.Free(offset));
            Assert.Equal(1024ul, list.FreeBytes);
            Assert.Equal(1, list.FreeBlockCount);
        }

        /// <summary>
        /// RANDOMIZED ALLOCATE-AND-FREE CHURN, asserting the two properties that matter: no two live
        /// suballocations ever overlap, and everything comes back. The seed is FIXED so a failure is reproducible
        /// from the row alone rather than being a story about a run nobody has.
        /// <para>
        /// The overlap assertion is the one that earns the whole file. A split or a merge that loses a boundary by
        /// one byte produces a free list whose totals still add up and whose ranges hand the same bytes to two
        /// resources, and on a real device that is a texture rendering into a vertex buffer.
        /// </para>
        /// </summary>
        [Fact]
        public void RandomizedChurn_NeverOverlapsAndFullyReclaims()
        {
            const ulong capacity = 1 << 20;
            ulong[] alignments = [1, 4, 16, 64, 256, 1024];

            var random = new Random(20260806);
            var list = new VulkanMemoryFreeList(capacity);
            var live = new Dictionary<ulong, ulong>();

            for (int step = 0; step < 6000; step++)
            {
                bool freeing = live.Count > 0 && random.Next(100) < 45;

                if (freeing)
                {
                    ulong victim = Nth(live, random.Next(live.Count));
                    list.Free(victim);
                    live.Remove(victim);
                    continue;
                }

                ulong size = (ulong)random.Next(1, 8192);
                ulong alignment = alignments[random.Next(alignments.Length)];

                if (!list.TryAllocate(size, alignment, out ulong offset)) continue;

                Assert.Equal(0ul, offset % alignment);
                Assert.True(offset + size <= capacity,
                    $"step {step}: [{offset}, {offset + size}) runs past the {capacity}-byte chunk");

                foreach (KeyValuePair<ulong, ulong> other in live)
                {
                    bool disjoint = offset + size <= other.Key || other.Key + other.Value <= offset;
                    Assert.True(disjoint,
                        $"step {step}: [{offset}, {offset + size}) overlaps [{other.Key}, "
                        + $"{other.Key + other.Value})");
                }

                live.Add(offset, size);
                Assert.Equal(SumOf(live), list.UsedBytes);
            }

            foreach (ulong offset in new List<ulong>(live.Keys)) list.Free(offset);

            Assert.Equal(0, list.LiveCount);
            Assert.Equal(0ul, list.UsedBytes);
            Assert.Equal(capacity, list.FreeBytes);

            // ONE range, which is the coalescing half. The byte total above would be satisfied by a thousand
            // one-byte ranges, and a chunk in that state can never satisfy another request.
            Assert.Equal(1, list.FreeBlockCount);
            Assert.Equal(capacity, list.LargestFreeBlock);
            Assert.True(list.TryAllocate(capacity, 1, out ulong whole));
            Assert.Equal(0ul, whole);
        }

        static ulong Nth(Dictionary<ulong, ulong> live, int index)
        {
            foreach (ulong key in live.Keys)
            {
                if (index-- == 0) return key;
            }

            throw new InvalidOperationException("The index was past the end of the live set.");
        }

        static ulong SumOf(Dictionary<ulong, ulong> live)
        {
            ulong total = 0;
            foreach (ulong size in live.Values) total += size;
            return total;
        }
    }
}
