using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The chunk's own contract, decisions V-M2 and V-M3: mapped once at creation and never unmapped, flush and
    /// invalidate free on a coherent type and real on a non-coherent one, and the atom-size isolation that keeps a
    /// widened range from reaching a neighbour.
    /// </summary>
    public sealed class VulkanMemoryChunkTests
    {
        const VulkanMemoryTrait Local = VulkanMemoryTrait.DeviceLocal;
        const VulkanMemoryTrait Visible = VulkanMemoryTrait.HostVisible;
        const VulkanMemoryTrait Coherent = VulkanMemoryTrait.HostCoherent;
        const VulkanMemoryTrait Cached = VulkanMemoryTrait.HostCached;

        /// <summary>
        /// MAPPED ONCE, AT CREATION (V-M3), and the fake throws if anything maps a second time. Not lazily on
        /// first use: a lazy map would be a native call on whatever path first wrote through the pointer, which is
        /// exactly the frame path the uniform ring exists to keep call-free.
        /// </summary>
        [Fact]
        public void AHostVisibleChunk_IsMappedExactlyOnceAtCreation()
        {
            var api = new FakeVulkanDeviceMemoryApi();
            var chunk = Chunk(api, Visible | Coherent, size: 4096);

            Assert.Equal(1, api.MapCount);
            Assert.NotEqual(0, chunk.MappedPointer);

            // Every suballocation reads its pointer off the chunk's, so no further mapping happens no matter how
            // many allocations are taken.
            for (int i = 0; i < 16; i++) Assert.True(chunk.TryAllocate(64, 1, out _));

            Assert.Equal(1, api.MapCount);
        }

        /// <summary>A device-local chunk is never mapped at all, because there is nothing to map: mapping a
        /// non-host-visible type is invalid, not merely pointless.</summary>
        [Fact]
        public void ADeviceLocalChunk_IsNeverMapped()
        {
            var api = new FakeVulkanDeviceMemoryApi();
            var chunk = Chunk(api, Local, size: 4096);

            Assert.Equal(0, api.MapCount);
            Assert.Equal(0, chunk.MappedPointer);

            Assert.True(chunk.TryAllocate(64, 1, out VulkanMemoryAllocation allocation));
            Assert.Equal(0, allocation.MappedPointer);
        }

        /// <summary>An allocation's pointer is the chunk's base plus its own offset, which is the whole of the
        /// persistent-mapping win: a resource holds a stable address for the chunk's life with no map call on any
        /// path.</summary>
        [Fact]
        public void AnAllocationsPointer_IsTheChunkBasePlusItsOffset()
        {
            var api = new FakeVulkanDeviceMemoryApi();
            var chunk = Chunk(api, Visible | Coherent, size: 4096);

            Assert.True(chunk.TryAllocate(100, 1, out VulkanMemoryAllocation first));
            Assert.True(chunk.TryAllocate(100, 1, out VulkanMemoryAllocation second));

            Assert.Equal(chunk.MappedPointer, first.MappedPointer);
            Assert.Equal(chunk.MappedPointer + (nint)second.Offset, second.MappedPointer);
            Assert.Equal(100ul, second.Offset);
        }

        /// <summary>
        /// THERE IS NO UNMAP ON THE NATIVE SEAM, and that is the structural half of "never unmapped". A
        /// behavioural test can only show that nothing unmapped on the paths it walked. The interface having no
        /// such member is what makes the alternative inexpressible, so the Direct3D 11 backend's
        /// map-and-unmap-per-record dance cannot be ported across by analogy.
        /// </summary>
        [Fact]
        public void TheNativeMemorySeam_HasNoUnmapMember()
        {
            string[] members = typeof(IVulkanDeviceMemoryApi)
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(m => m.Name)
                .ToArray();

            Assert.DoesNotContain(members, name => name.Contains("Unmap", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(5, members.Length);
        }

        /// <summary>
        /// A COHERENT CHUNK FLUSHES AND INVALIDATES NOTHING, and that is free rather than cheap: coherent memory
        /// needs neither by definition, and <c>vkQueueSubmit</c> performs an implicit host-write availability
        /// operation for it. That is what every ladder preferring coherent buys, and why the incumbent having
        /// neither call anywhere has never been noticed.
        /// </summary>
        [Fact]
        public void ACoherentChunk_MakesNoFlushAndNoInvalidateCalls()
        {
            var api = new FakeVulkanDeviceMemoryApi();
            var chunk = Chunk(api, Visible | Coherent, size: 4096);

            Assert.True(chunk.TryAllocate(300, 1, out VulkanMemoryAllocation allocation));

            allocation.Flush();
            allocation.Flush(16, 32);
            allocation.Invalidate();
            allocation.Invalidate(16, 32);

            Assert.Empty(api.Flushes);
            Assert.Empty(api.Invalidates);
        }

        /// <summary>
        /// A CACHED, NON-COHERENT CHUNK EMITS THE REAL CALLS, which is the readback path section 9.1 says makes
        /// the invalidate real code rather than a defensive branch. The range that reaches the driver is widened
        /// to the atom boundary and is expressed in CHUNK-relative bytes.
        /// </summary>
        [Fact]
        public void ANonCoherentChunk_EmitsWidenedFlushAndInvalidateRanges()
        {
            var api = new FakeVulkanDeviceMemoryApi();
            var chunk = Chunk(api, Visible | Cached, size: 4096, atomSize: 64);

            Assert.True(chunk.TryAllocate(200, 1, out VulkanMemoryAllocation first));
            Assert.True(chunk.TryAllocate(200, 1, out VulkanMemoryAllocation second));

            second.Flush(10, 20);
            second.Invalidate();

            FakeVulkanMappedRange flushed = Assert.Single(api.Flushes);
            Assert.Equal(chunk.Memory, flushed.Memory);
            Assert.Equal(0ul, flushed.Offset % 64);
            Assert.True(flushed.Offset <= second.Offset + 10);
            Assert.True(flushed.Offset + flushed.Size >= second.Offset + 30);

            FakeVulkanMappedRange invalidated = Assert.Single(api.Invalidates);
            Assert.Equal(0ul, invalidated.Offset % 64);
            Assert.Equal(second.Offset, invalidated.Offset);
            Assert.Equal(second.Size, invalidated.Size);

            // The first allocation is untouched by the second's ranges, which is the property the isolation below
            // exists for.
            Assert.True(invalidated.Offset >= first.Offset + first.Size);
        }

        /// <summary>
        /// THE ISOLATION RULE, and it is a correctness fix rather than tidiness. On a host-visible NON-coherent
        /// chunk every suballocation starts on a <c>nonCoherentAtomSize</c> boundary and occupies a whole number
        /// of atoms, so a widened flush or invalidate range can never reach into a neighbour. Without it, an
        /// invalidate over one allocation would discard the host's cached view of the next one's un-flushed
        /// writes.
        /// </summary>
        [Fact]
        public void ANonCoherentChunk_IsolatesEverySuballocationToAnAtomBoundary()
        {
            var api = new FakeVulkanDeviceMemoryApi();
            var chunk = Chunk(api, Visible | Cached, size: 8192, atomSize: 256);

            var made = new VulkanMemoryAllocation[12];
            for (int i = 0; i < made.Length; i++)
            {
                Assert.True(chunk.TryAllocate((ulong)(17 + i), 1, out made[i]));

                Assert.Equal(0ul, made[i].Offset % 256);
                Assert.Equal(0ul, made[i].Size % 256);
                Assert.True(made[i].Size >= (ulong)(17 + i));
            }

            // And every widened range stays inside its own allocation, which is the property the alignment buys.
            for (int i = 0; i < made.Length; i++)
            {
                made[i].Invalidate();

                FakeVulkanMappedRange range = api.Invalidates[i];
                Assert.True(range.Offset >= made[i].Offset);
                Assert.True(range.Offset + range.Size <= made[i].Offset + made[i].Size);
            }
        }

        /// <summary>A COHERENT chunk does no such rounding, because it never widens a range. Rounding there would
        /// waste up to an atom per allocation for nothing, and on a 256-byte atom that is real memory on every
        /// small uniform allocation.</summary>
        [Fact]
        public void ACoherentChunk_DoesNotRoundToTheAtomSize()
        {
            var api = new FakeVulkanDeviceMemoryApi();
            var chunk = Chunk(api, Visible | Coherent, size: 8192, atomSize: 256);

            Assert.True(chunk.TryAllocate(17, 1, out VulkanMemoryAllocation first));
            Assert.True(chunk.TryAllocate(17, 1, out VulkanMemoryAllocation second));

            Assert.Equal(17ul, first.Size);
            Assert.Equal(17ul, second.Size);
            Assert.Equal(17ul, second.Offset);
        }

        /// <summary>A range that runs past the end of its own allocation throws. Ranges are relative to the
        /// allocation, not to the chunk, and the mistake this catches is the one that reaches a neighbour's
        /// bytes.</summary>
        [Fact]
        public void ARangePastTheEndOfItsAllocation_Throws()
        {
            var api = new FakeVulkanDeviceMemoryApi();
            var chunk = Chunk(api, Visible | Cached, size: 4096, atomSize: 64);

            Assert.True(chunk.TryAllocate(128, 1, out VulkanMemoryAllocation allocation));

            Assert.Throws<ArgumentOutOfRangeException>(() => allocation.Flush(0, allocation.Size + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => allocation.Invalidate(allocation.Size, 1));
        }

        /// <summary>Asking a device-local chunk to flush is engine-internal misuse: there is no mapping to make
        /// available in either direction, and the caller is treating a static allocation as an upload one.</summary>
        [Fact]
        public void FlushingADeviceLocalAllocation_Throws()
        {
            var api = new FakeVulkanDeviceMemoryApi();
            var chunk = Chunk(api, Local, size: 4096);

            Assert.True(chunk.TryAllocate(128, 1, out VulkanMemoryAllocation allocation));

            Assert.Throws<InvalidOperationException>(() => allocation.Flush());
            Assert.Throws<InvalidOperationException>(() => allocation.Invalidate());
        }

        /// <summary>Destroying is idempotent and reports whether THIS call ended the chunk, which is what keeps
        /// the allocation counter honest across a double drain without a second flag.</summary>
        [Fact]
        public void Destroy_IsIdempotentAndReportsWhichCallEndedIt()
        {
            var api = new FakeVulkanDeviceMemoryApi();
            var chunk = Chunk(api, Visible | Coherent, size: 4096);

            Assert.True(chunk.Destroy());
            Assert.Equal(1, api.FreeCount);
            Assert.True(chunk.IsDestroyed);

            Assert.False(chunk.Destroy());
            Assert.False(chunk.Forget());
            Assert.Equal(1, api.FreeCount);
        }

        /// <summary>Forgetting makes NO native call, for a device that is already dead: its memory went with the
        /// device, so a free now is a call against freed memory that aborts through the loader.</summary>
        [Fact]
        public void Forget_MakesNoNativeCall()
        {
            var api = new FakeVulkanDeviceMemoryApi();
            var chunk = Chunk(api, Visible | Coherent, size: 4096);

            Assert.True(chunk.Forget());
            Assert.Equal(0, api.FreeCount);
            Assert.True(chunk.IsDestroyed);
            Assert.Single(api.LiveHandles);

            Assert.False(chunk.Destroy());
            Assert.Equal(0, api.FreeCount);
        }

        /// <summary>Using a destroyed chunk throws rather than handing back a dangling offset into freed
        /// memory.</summary>
        [Fact]
        public void UsingADestroyedChunk_Throws()
        {
            var api = new FakeVulkanDeviceMemoryApi();
            var chunk = Chunk(api, Visible | Coherent, size: 4096);

            Assert.True(chunk.TryAllocate(64, 1, out VulkanMemoryAllocation allocation));
            chunk.Destroy();

            Assert.Throws<InvalidOperationException>(() => chunk.TryAllocate(64, 1, out _));
            Assert.Throws<InvalidOperationException>(() => chunk.Free(allocation.Offset));
        }

        /// <summary>A dedicated allocation names a buffer or an image, never both:
        /// <c>VUID-VkMemoryDedicatedAllocateInfo-image-01432</c> permits at most one.</summary>
        [Fact]
        public void ADedicatedTargetNamingBothABufferAndAnImage_Throws()
        {
            var api = new FakeVulkanDeviceMemoryApi();

            Assert.Throws<ArgumentException>(() => new VulkanMemoryChunk(
                api, 0, Visible | Coherent, VulkanMemoryTiling.Linear, 4096, 64,
                new VulkanDedicatedTarget(Buffer: 7, Image: 9), isDedicated: true));
        }

        /// <summary>A default allocation refers to nothing, which is what a value stored before it was made looks
        /// like. Every member that would need a chunk says so rather than dereferencing null.</summary>
        [Fact]
        public void ADefaultAllocation_RefersToNothing()
        {
            VulkanMemoryAllocation allocation = default;

            Assert.False(allocation.IsValid);
            Assert.Equal(0ul, allocation.Memory);
            Assert.Equal(0, allocation.MappedPointer);
            Assert.False(allocation.IsDedicated);
            Assert.Throws<InvalidOperationException>(() => allocation.Flush());
        }

        static VulkanMemoryChunk Chunk(FakeVulkanDeviceMemoryApi api, VulkanMemoryTrait traits, ulong size,
            ulong atomSize = 64)
            => new(api, memoryTypeIndex: 0, traits, VulkanMemoryTiling.Linear, size, atomSize,
                VulkanDedicatedTarget.None, isDedicated: false);
    }
}
