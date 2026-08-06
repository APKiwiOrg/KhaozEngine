using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The block suballocator as a whole, decisions V-M1 to V-M4 and section 9.1: pools keyed by
    /// <c>(memoryTypeIndex, linear|optimal)</c>, the dedicated path, the <c>vkAllocateMemory</c> counter MV6 reads,
    /// and the retire ordering that keeps memory out of the driver's hands until the timeline has passed.
    /// <para>
    /// The retire rows drive the REAL <c>VulkanRetireList</c> and the REAL <c>VulkanTimeline</c> from row 5, over
    /// a fake semaphore, rather than a stub of their own. The ordering being asserted is a property of those two
    /// types plus this one, and a stub would only prove that the allocator calls something.
    /// </para>
    /// </summary>
    public sealed class VulkanMemoryAllocatorTests
    {
        const VulkanMemoryTrait Local = VulkanMemoryTrait.DeviceLocal;
        const VulkanMemoryTrait Visible = VulkanMemoryTrait.HostVisible;
        const VulkanMemoryTrait Coherent = VulkanMemoryTrait.HostCoherent;
        const VulkanMemoryTrait Cached = VulkanMemoryTrait.HostCached;

        /// <summary>The point of the whole type: many resources, one <c>vkAllocateMemory</c>. MV6's bet is that
        /// this holds well enough to keep the live count under a quarter of the device limit.</summary>
        [Fact]
        public void SmallAllocations_ShareOneChunk()
        {
            var fixture = new Fixture();

            for (int i = 0; i < 8; i++) fixture.Allocate(VulkanMemoryUsage.DeviceLocal, size: 256);

            Assert.Equal(1, fixture.Api.AllocateCount);
            Assert.Equal(1, fixture.Allocator.LiveDeviceAllocations);
            Assert.Equal(1, fixture.Allocator.PoolCount);
            Assert.Equal(0, fixture.Allocator.DedicatedChunkCount);
        }

        /// <summary>
        /// LINEAR AND OPTIMAL NEVER SHARE A CHUNK (V-M2), which is the entire <c>bufferImageGranularity</c>
        /// implementation. Two allocations of the same usage and the same memory type land in two chunks purely
        /// because their tiling differs, so a buffer and an optimal-tiling image can never share a granularity
        /// page and there is no rounding anywhere to get wrong.
        /// </summary>
        [Fact]
        public void LinearAndOptimalTiling_NeverShareAChunk()
        {
            var fixture = new Fixture();

            VulkanMemoryAllocation linear = fixture.Allocate(
                VulkanMemoryUsage.DeviceLocal, 256, VulkanMemoryTiling.Linear);
            VulkanMemoryAllocation optimal = fixture.Allocate(
                VulkanMemoryUsage.DeviceLocal, 256, VulkanMemoryTiling.Optimal);

            Assert.Equal(2, fixture.Api.AllocateCount);
            Assert.Equal(2, fixture.Allocator.PoolCount);
            Assert.NotEqual(linear.Memory, optimal.Memory);

            // Both chunks came off the SAME memory type, so tiling really is the only thing that separated them.
            Assert.Equal(fixture.Api.Allocations[0].MemoryTypeIndex, fixture.Api.Allocations[1].MemoryTypeIndex);
        }

        /// <summary>Two usages that resolve to different memory types get different pools, which is the other
        /// half of the key.</summary>
        [Fact]
        public void DifferentMemoryTypes_GetDifferentPools()
        {
            var fixture = new Fixture();

            VulkanMemoryAllocation stat = fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 256);
            VulkanMemoryAllocation upload = fixture.Allocate(VulkanMemoryUsage.Upload, 256);

            Assert.Equal(2, fixture.Allocator.PoolCount);
            Assert.NotEqual(stat.Memory, upload.Memory);
            Assert.Equal(0u, fixture.Api.Allocations[0].MemoryTypeIndex);
            Assert.Equal(1u, fixture.Api.Allocations[1].MemoryTypeIndex);
        }

        /// <summary>EXHAUSTION MAKES A NEW CHUNK rather than failing. A full chunk is the ordinary state of a busy
        /// pool, and the second chunk joins the same pool.</summary>
        [Fact]
        public void AFullChunk_MakesAnotherOneInTheSamePool()
        {
            var fixture = new Fixture(chunkSize: 4096, dedicatedThreshold: 4096);

            var made = new List<VulkanMemoryAllocation>();
            for (int i = 0; i < 6; i++) made.Add(fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 1000));

            Assert.Equal(2, fixture.Api.AllocateCount);
            Assert.Equal(1, fixture.Allocator.PoolCount);
            Assert.Equal(2, fixture.Allocator.LiveDeviceAllocations);

            // Four fitted in the first chunk (4000 of 4096) and the rest opened the second.
            Assert.Equal(made[0].Memory, made[3].Memory);
            Assert.NotEqual(made[3].Memory, made[4].Memory);
        }

        /// <summary>A request at or above the threshold gets its own <c>vkAllocateMemory</c>, sized to the
        /// request rather than to a whole chunk, and stays out of the pools.</summary>
        [Fact]
        public void ARequestAtTheThreshold_GetsItsOwnAllocation()
        {
            var fixture = new Fixture(chunkSize: 4096, dedicatedThreshold: 1024);

            VulkanMemoryAllocation allocation = fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 1024);

            Assert.True(allocation.IsDedicated);
            Assert.Equal(1, fixture.Allocator.DedicatedChunkCount);
            Assert.Equal(0, fixture.Allocator.PoolCount);
            Assert.Equal(1024ul, fixture.Api.Allocations[0].Size);
            Assert.Equal(0ul, allocation.Offset);
        }

        /// <summary>A request larger than a whole chunk is dedicated for the arithmetic reason as well as the
        /// policy one: no chunk of the configured size could ever hold it.</summary>
        [Fact]
        public void ARequestLargerThanAChunk_GetsItsOwnAllocation()
        {
            var fixture = new Fixture(chunkSize: 4096, dedicatedThreshold: 1_000_000);

            VulkanMemoryAllocation allocation = fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 9000);

            Assert.True(allocation.IsDedicated);
            Assert.Equal(9000ul, fixture.Api.Allocations[0].Size);
        }

        /// <summary>
        /// THE DRIVER'S OWN ANSWER IS HONOURED, in both of its forms and for a request far below the threshold.
        /// <c>requiresDedicatedAllocation</c> is a spec requirement rather than a hint, and
        /// <c>prefersDedicatedAllocation</c> is only ever set when the driver has a compression or fast-clear path
        /// it can take on memory it owns outright. The target reaches
        /// <c>VkMemoryDedicatedAllocateInfo</c> unchanged.
        /// </summary>
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void TheDriverAskingForDedicated_IsHonoured(bool prefers, bool requires)
        {
            var fixture = new Fixture(chunkSize: 4096, dedicatedThreshold: 4096);

            var request = new VulkanMemoryRequest(
                Size: 64,
                Alignment: 16,
                MemoryTypeBits: uint.MaxValue,
                Usage: VulkanMemoryUsage.DeviceLocal,
                Tiling: VulkanMemoryTiling.Optimal,
                PrefersDedicated: prefers,
                RequiresDedicated: requires,
                DedicatedTarget: new VulkanDedicatedTarget(Buffer: 0, Image: 0xabc));

            VulkanMemoryAllocation allocation = fixture.Allocator.Allocate(request);

            Assert.True(allocation.IsDedicated);
            Assert.Equal(1, fixture.Allocator.DedicatedChunkCount);
            Assert.Equal(0xabcul, fixture.Api.Allocations[0].Dedicated.Image);
            Assert.Equal(0ul, fixture.Api.Allocations[0].Dedicated.Buffer);

            // And it counts in the same counter as a pooled chunk, because it is the same native call.
            Assert.Equal(1, fixture.Allocator.LiveDeviceAllocations);
            Assert.Equal(1, fixture.Allocator.LifetimeDeviceAllocations);
        }

        /// <summary>The counter counts CHUNKS, which is what <c>maxMemoryAllocationCount</c> is about, and the
        /// lifetime figure keeps climbing while the live one comes back down. A live count that stays flat while
        /// the lifetime climbs is a pool churning chunks, which is a different problem from a large one.</summary>
        [Fact]
        public void TheCounterCountsChunksAndSeparatesLiveFromLifetime()
        {
            var fixture = new Fixture(chunkSize: 4096, dedicatedThreshold: 1024);

            VulkanMemoryAllocation first = fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 2048);
            VulkanMemoryAllocation second = fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 2048);

            Assert.Equal(2, fixture.Allocator.LiveDeviceAllocations);
            Assert.Equal(2, fixture.Allocator.LifetimeDeviceAllocations);

            fixture.FreeAndDrain(first);
            Assert.Equal(1, fixture.Allocator.LiveDeviceAllocations);
            Assert.Equal(2, fixture.Allocator.LifetimeDeviceAllocations);

            fixture.FreeAndDrain(second);
            Assert.Equal(0, fixture.Allocator.LiveDeviceAllocations);
            Assert.Equal(2, fixture.Allocator.LifetimeDeviceAllocations);
            Assert.Empty(fixture.Api.LiveHandles);
        }

        /// <summary>
        /// THE ROW THIS WHOLE FILE IS FOR: a chunk that empties is RETIRED, not freed. Its
        /// <c>vkFreeMemory</c> runs only once the timeline has passed the value recorded at free time, so memory
        /// is never returned to the driver while a submission that could still be reading it is outstanding. That
        /// is V-F9 applied to memory rather than to objects, and it is why the allocator takes a retirement hook
        /// instead of calling the seam.
        /// </summary>
        [Fact]
        public void AnEmptiedChunk_IsRetiredAndFreedOnlyAfterTheTimelinePasses()
        {
            var fixture = new Fixture(chunkSize: 4096, dedicatedThreshold: 1024);

            // Three submissions have been made, so a resource disposed now could still be read by any of them.
            fixture.Timeline.NextSubmitValue();
            fixture.Timeline.NextSubmitValue();
            ulong outstanding = fixture.Timeline.NextSubmitValue();

            VulkanMemoryAllocation allocation = fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 2048);
            Assert.Equal(1, fixture.Allocator.LiveDeviceAllocations);

            fixture.Allocator.Free(allocation);

            // NOTHING has been freed yet, and the destroy is being held.
            Assert.Equal(0, fixture.Api.FreeCount);
            Assert.Single(fixture.Api.LiveHandles);
            Assert.Equal(1, fixture.Retired.Count);
            Assert.Equal(1, fixture.Allocator.LiveDeviceAllocations);

            // A drain short of the outstanding value releases nothing.
            Assert.Equal(0, fixture.Retired.Drain(outstanding - 1));
            Assert.Equal(0, fixture.Api.FreeCount);

            // The GPU reaches it, and only then does the memory go back.
            fixture.Semaphore.Completed = outstanding;
            Assert.Equal(1, fixture.Retired.Drain(fixture.Timeline.CompletedValue));
            Assert.Equal(1, fixture.Api.FreeCount);
            Assert.Empty(fixture.Api.LiveHandles);
            Assert.Equal(0, fixture.Allocator.LiveDeviceAllocations);
        }

        /// <summary>
        /// A POOL KEEPS ITS LAST CHUNK. Retiring the only chunk of a pool that is about to be used again turns a
        /// load-unload cycle into a <c>vkAllocateMemory</c> per iteration, which is the allocation storm this
        /// allocator exists to remove. A second empty chunk has no such argument and goes.
        /// </summary>
        [Fact]
        public void APoolKeepsItsLastChunkAndRetiresTheRest()
        {
            var fixture = new Fixture(chunkSize: 4096, dedicatedThreshold: 4096);

            var made = new List<VulkanMemoryAllocation>();
            for (int i = 0; i < 6; i++) made.Add(fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 1000));
            Assert.Equal(2, fixture.Api.AllocateCount);

            foreach (VulkanMemoryAllocation allocation in made) fixture.Allocator.Free(allocation);
            fixture.Drain();

            // One of the two went back, and one is still resident for the next load.
            Assert.Equal(1, fixture.Api.FreeCount);
            Assert.Equal(1, fixture.Allocator.LiveDeviceAllocations);

            // And the resident chunk is reused rather than a third being made.
            fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 1000);
            Assert.Equal(2, fixture.Api.AllocateCount);
        }

        /// <summary>Teardown frees every chunk immediately, which is correct because the device's own
        /// <c>vkDeviceWaitIdle</c> has already returned by the time it runs.</summary>
        [Fact]
        public void Dispose_FreesEveryChunkImmediately()
        {
            var fixture = new Fixture(chunkSize: 4096, dedicatedThreshold: 2048);

            fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 256);
            fixture.Allocate(VulkanMemoryUsage.Upload, 256, VulkanMemoryTiling.Optimal);
            fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 3000);
            Assert.Equal(3, fixture.Allocator.LiveDeviceAllocations);

            fixture.Allocator.Dispose();

            Assert.Equal(3, fixture.Api.FreeCount);
            Assert.Empty(fixture.Api.LiveHandles);
            Assert.Equal(0, fixture.Allocator.LiveDeviceAllocations);
            Assert.Equal(0, fixture.Allocator.PoolCount);
            Assert.Equal(0, fixture.Allocator.DedicatedChunkCount);

            // Idempotent, like every other teardown in this package.
            fixture.Allocator.Dispose();
            Assert.Equal(3, fixture.Api.FreeCount);
        }

        /// <summary>
        /// ABANDON FREES NOTHING, for a device that is already dead or was lost by the teardown wait. Its memory
        /// went with the device, so a <c>vkFreeMemory</c> now is a call against freed memory, which aborts the
        /// process through the Vulkan loader rather than failing quietly.
        /// </summary>
        [Fact]
        public void Abandon_DropsEveryChunkWithoutFreeingIt()
        {
            var fixture = new Fixture(chunkSize: 4096, dedicatedThreshold: 2048);

            fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 256);
            fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 3000);

            Assert.Equal(2, fixture.Allocator.Abandon());
            Assert.Equal(0, fixture.Api.FreeCount);
            Assert.Equal(2, fixture.Api.LiveHandles.Count);
            Assert.Equal(0, fixture.Allocator.LiveDeviceAllocations);

            Assert.Equal(0, fixture.Allocator.Abandon());
        }

        /// <summary>A free arriving after teardown is a no-op rather than a throw, because a resource wrapper
        /// outliving its device is ordinary at teardown and its chunk's memory has already gone. That is the same
        /// rule the liveness token applies to every other destroy in this package.</summary>
        [Fact]
        public void FreeingAfterTeardown_IsANoOp()
        {
            var fixture = new Fixture();

            VulkanMemoryAllocation allocation = fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 256);
            fixture.Allocator.Dispose();

            fixture.Allocator.Free(allocation);
            Assert.Equal(1, fixture.Api.FreeCount);
            Assert.Equal(0, fixture.Retired.Count);
        }

        /// <summary>Allocating after teardown throws, unlike freeing: there is nothing left to suballocate out of,
        /// and handing back a valid-looking allocation into freed memory is the worse answer.</summary>
        [Fact]
        public void AllocatingAfterTeardown_Throws()
        {
            var fixture = new Fixture();
            fixture.Allocator.Dispose();

            Assert.Throws<InvalidOperationException>(
                () => fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 256));
        }

        /// <summary>A zero-byte request is refused before any type is chosen: <c>vkAllocateMemory</c> rejects an
        /// <c>allocationSize</c> of 0 and a suballocation of 0 has no offset that means anything.</summary>
        [Fact]
        public void AZeroSizeRequest_Throws()
        {
            var fixture = new Fixture();

            Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Allocate(VulkanMemoryUsage.Upload, 0));
            Assert.Equal(0, fixture.Api.AllocateCount);
        }

        /// <summary>An alignment that is not a power of two did not come off a
        /// <c>VkMemoryRequirements</c>.</summary>
        [Fact]
        public void ANonPowerOfTwoAlignment_Throws()
        {
            var fixture = new Fixture();

            Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Allocator.Allocate(new VulkanMemoryRequest(
                Size: 64, Alignment: 24, MemoryTypeBits: uint.MaxValue,
                Usage: VulkanMemoryUsage.Upload, Tiling: VulkanMemoryTiling.Linear)));
        }

        /// <summary>The ring's hard requirement (V-M4) reaches the allocator: a device with no host-visible
        /// coherent type refuses rather than falling back to a flushed path, and the message says which decision
        /// it is refusing under.</summary>
        [Fact]
        public void TheRingOnADeviceWithNoCoherentType_ThrowsNamingTheDecision()
        {
            IReadOnlyList<VulkanMemoryTypeInfo> types = [new(0, 0, Local), new(1, 1, Visible | Cached)];
            var fixture = new Fixture(types: types);

            var thrown = Assert.Throws<InvalidOperationException>(
                () => fixture.Allocate(VulkanMemoryUsage.Ring, 256));

            Assert.Contains("V-M4", thrown.Message, StringComparison.Ordinal);
            Assert.Equal(0, fixture.Api.AllocateCount);
        }

        /// <summary>
        /// AN ALLOCATION ON A NON-COHERENT TYPE IS ATOM-ISOLATED END TO END, through the allocator rather than
        /// through the chunk directly. Readback is the usage that reaches such a type on a real device, so this is
        /// the path a widened invalidate actually runs on.
        /// </summary>
        [Fact]
        public void ReadbackOnACachedNonCoherentType_IsAtomIsolated()
        {
            IReadOnlyList<VulkanMemoryTypeInfo> types = [new(0, 0, Local), new(1, 1, Visible | Cached)];
            var fixture = new Fixture(types: types, atomSize: 128);

            VulkanMemoryAllocation first = fixture.Allocate(VulkanMemoryUsage.Readback, 100);
            VulkanMemoryAllocation second = fixture.Allocate(VulkanMemoryUsage.Readback, 100);

            Assert.Equal(0ul, first.Offset % 128);
            Assert.Equal(0ul, second.Offset % 128);
            Assert.Equal(128ul, first.Size);
            Assert.Equal(128ul, second.Size);
            Assert.True(second.Offset >= first.Offset + first.Size);

            second.Invalidate();
            FakeVulkanMappedRange range = Assert.Single(fixture.Api.Invalidates);
            Assert.Equal(second.Offset, range.Offset);
            Assert.Equal(second.Size, range.Size);
        }

        /// <summary>
        /// MV6's exit criterion is announced while the run is happening rather than only in the reading
        /// afterwards, and it is said ONCE per device rather than once per allocation.
        /// </summary>
        [Fact]
        public void CrossingAQuarterOfTheAllocationLimit_WarnsOnceNamingMV6()
        {
            var fixture = new Fixture(chunkSize: 1024, dedicatedThreshold: 512, maxAllocationCount: 8);

            fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 600);
            Assert.Empty(fixture.Logger.Warns);

            fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 600);
            Assert.Single(fixture.Logger.Warns);
            Assert.Contains("MV6", fixture.Logger.Warns[0], StringComparison.Ordinal);

            fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 600);
            fixture.Allocate(VulkanMemoryUsage.DeviceLocal, 600);
            Assert.Single(fixture.Logger.Warns);
        }

        /// <summary>Freeing something that was never allocated is engine-internal misuse and says so.</summary>
        [Fact]
        public void FreeingADefaultAllocation_Throws()
        {
            var fixture = new Fixture();

            Assert.Throws<ArgumentException>(() => fixture.Allocator.Free(default));
        }

        /// <summary>
        /// The two policy numbers are pinned, so moving either is a deliberate edit somebody had to read the
        /// reasoning for. They are policy choices rather than measurements, and MV6's resident-memory reading
        /// against the incumbent on the same scene is what would move them.
        /// </summary>
        [Fact]
        public void TheDefaultPolicyNumbers_ArePinned()
        {
            Assert.Equal(64UL * 1024 * 1024, VulkanMemoryAllocator.DefaultChunkSize);
            Assert.Equal(16UL * 1024 * 1024, VulkanMemoryAllocator.DefaultDedicatedThreshold);
            Assert.Equal(VulkanMemoryAllocator.DefaultChunkSize / 4,
                VulkanMemoryAllocator.DefaultDedicatedThreshold);
        }

        // The device-free rig: the fake native seam, the REAL timeline and retire list from row 5 over a fake
        // semaphore, and the allocator wired to both exactly as VulkanGpuDevice wires them.
        sealed class Fixture
        {
            internal Fixture(ulong chunkSize = 4096, ulong dedicatedThreshold = 100_000, ulong atomSize = 64,
                uint maxAllocationCount = 4096, IReadOnlyList<VulkanMemoryTypeInfo>? types = null)
            {
                Semaphore = new FakeVulkanTimelineSemaphore();
                Timeline = new VulkanTimeline(Semaphore);
                Retired = new VulkanRetireList(new RecordingLogger());
                Api = new FakeVulkanDeviceMemoryApi();
                Logger = new RecordingLogger();

                var facts = new VulkanMemoryFacts(types ?? Default, atomSize, maxAllocationCount);

                Allocator = new VulkanMemoryAllocator(Api, facts, new VulkanTimelineRetirement(Timeline, Retired),
                    chunkSize, dedicatedThreshold, Logger);
            }

            internal FakeVulkanTimelineSemaphore Semaphore { get; }

            internal VulkanTimeline Timeline { get; }

            internal VulkanRetireList Retired { get; }

            internal FakeVulkanDeviceMemoryApi Api { get; }

            internal RecordingLogger Logger { get; }

            internal VulkanMemoryAllocator Allocator { get; }

            // A discrete-card-shaped device: pure VRAM, a plain upload heap, and a cached readback heap.
            static IReadOnlyList<VulkanMemoryTypeInfo> Default =>
            [
                new(0, 0, Local),
                new(1, 1, Visible | Coherent),
                new(2, 1, Visible | Coherent | Cached),
            ];

            internal VulkanMemoryAllocation Allocate(VulkanMemoryUsage usage, ulong size,
                VulkanMemoryTiling tiling = VulkanMemoryTiling.Linear, ulong alignment = 16)
                => Allocator.Allocate(new VulkanMemoryRequest(size, alignment, uint.MaxValue, usage, tiling));

            internal void FreeAndDrain(in VulkanMemoryAllocation allocation)
            {
                Allocator.Free(allocation);
                Drain();
            }

            // Nothing has ever been submitted in most of these rows, so LastSubmitted is 0 and the very next
            // drain releases everything. That is the retire list's own documented behaviour rather than a
            // shortcut: a resource no submission has ever referenced is safe to destroy immediately.
            internal void Drain() => Retired.Drain(Timeline.CompletedValue);
        }
    }
}
