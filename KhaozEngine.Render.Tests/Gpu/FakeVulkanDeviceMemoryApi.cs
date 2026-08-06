using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>One <c>vkAllocateMemory</c> as the fake saw it.</summary>
    /// <param name="Handle">The pretend <c>VkDeviceMemory</c>.</param>
    /// <param name="MemoryTypeIndex">The type the allocator chose.</param>
    /// <param name="Size">The chunk size it asked for.</param>
    /// <param name="Dedicated">The <c>VkMemoryDedicatedAllocateInfo</c> target, or
    /// <see cref="VulkanDedicatedTarget.None"/>.</param>
    internal readonly record struct FakeVulkanAllocation(
        ulong Handle, uint MemoryTypeIndex, ulong Size, VulkanDedicatedTarget Dedicated);

    /// <summary>One <c>vkFlushMappedMemoryRanges</c> or <c>vkInvalidateMappedMemoryRanges</c>, as the driver
    /// would have received it: already widened to <c>nonCoherentAtomSize</c>.</summary>
    internal readonly record struct FakeVulkanMappedRange(ulong Memory, ulong Offset, ulong Size);

    /// <summary>
    /// The five native memory calls with no device behind them, so everything the block suballocator DECIDES (the
    /// pooling, the ladders, the splitting, the coalescing, the dedicated path, the map-once rule, the atom-size
    /// widening, the allocation counter and the retire ordering) is driven by a plain <c>[Fact]</c> on a machine
    /// with no Vulkan loader.
    /// <para>
    /// This is the point of <see cref="IVulkanDeviceMemoryApi"/> being an interface at all, and it matters more
    /// here than for the timeline: section 9.1 declines VMA and answers the "hand-rolled allocators are where
    /// memory corruption lives" counterargument with this code's own testability, so the tests have to reach every
    /// arithmetic decision without a driver.
    /// </para>
    /// <para>
    /// IT REFUSES A DOUBLE FREE AND A FREE OF SOMETHING IT NEVER HANDED OUT, by throwing. On a real device both
    /// are calls against freed memory, which abort the process through the Vulkan loader rather than failing
    /// quietly, so a fake that shrugged at them would be the one place the defect is survivable.
    /// </para>
    /// <para>
    /// A MAPPING IS REAL PINNED MEMORY, not a pretend address. Row 9's staged uploads and the uniform ring both
    /// WRITE through the pointer this hands back, so an invented address would crash the test run rather than
    /// fail it. Pinning a managed array keeps the lifetime bounded by the handle rather than by a manual free:
    /// a chunk nobody frees leaks one pin for the process, which for a test run is nothing.
    /// </para>
    /// </summary>
    internal sealed class FakeVulkanDeviceMemoryApi : IVulkanDeviceMemoryApi
    {
        readonly List<FakeVulkanAllocation> _allocations = new();
        readonly HashSet<ulong> _liveHandles = new();
        readonly HashSet<ulong> _mappedHandles = new();
        readonly Dictionary<ulong, GCHandle> _pinned = new();

        ulong _nextHandle = 0x1000;

        /// <summary>Every <c>vkAllocateMemory</c> made, in order. Its count IS the number MV6 reads.</summary>
        internal IReadOnlyList<FakeVulkanAllocation> Allocations => _allocations;

        /// <summary>How many <c>vkMapMemory</c> calls happened. One per host-visible chunk and never more, which
        /// is the map-once rule of V-M3.</summary>
        internal int MapCount { get; private set; }

        /// <summary>How many <c>vkFreeMemory</c> calls happened.</summary>
        internal int FreeCount { get; private set; }

        /// <summary>Every flushed range, already widened.</summary>
        internal List<FakeVulkanMappedRange> Flushes { get; } = new();

        /// <summary>Every invalidated range, already widened.</summary>
        internal List<FakeVulkanMappedRange> Invalidates { get; } = new();

        /// <summary>Handles allocated and not yet freed. Nonzero after a teardown that was meant to free
        /// everything is a leak.</summary>
        internal IReadOnlyCollection<ulong> LiveHandles => _liveHandles;

        /// <summary>How many <c>vkAllocateMemory</c> calls happened, ever.</summary>
        internal int AllocateCount => _allocations.Count;

        /// <inheritdoc/>
        public ulong Allocate(uint memoryTypeIndex, ulong size, VulkanDedicatedTarget dedicated)
        {
            if (size == 0) throw new InvalidOperationException("vkAllocateMemory rejects an allocationSize of 0.");

            ulong handle = _nextHandle;
            _nextHandle += 0x1000;

            _allocations.Add(new FakeVulkanAllocation(handle, memoryTypeIndex, size, dedicated));
            _liveHandles.Add(handle);
            return handle;
        }

        /// <inheritdoc/>
        public nint MapWhole(ulong memory, ulong size)
        {
            RequireLive(memory, "vkMapMemory");

            if (!_mappedHandles.Add(memory))
            {
                throw new InvalidOperationException(
                    "A native Vulkan memory chunk was mapped twice. Host-visible chunks are mapped once at "
                    + "creation and never unmapped (V-M3), so a second map means somebody added a lazy or "
                    + "per-use mapping path.");
            }

            MapCount++;

            // REAL memory, pinned, so a caller may write through the pointer and read the bytes back. See the
            // class note: a pretend address crashes rather than fails once anything stages an upload through one.
            GCHandle handle = GCHandle.Alloc(new byte[size], GCHandleType.Pinned);
            _pinned[memory] = handle;
            return handle.AddrOfPinnedObject();
        }

        /// <inheritdoc/>
        public void Flush(ulong memory, ulong offset, ulong size)
        {
            RequireLive(memory, "vkFlushMappedMemoryRanges");
            Flushes.Add(new FakeVulkanMappedRange(memory, offset, size));
        }

        /// <inheritdoc/>
        public void Invalidate(ulong memory, ulong offset, ulong size)
        {
            RequireLive(memory, "vkInvalidateMappedMemoryRanges");
            Invalidates.Add(new FakeVulkanMappedRange(memory, offset, size));
        }

        /// <inheritdoc/>
        public void Free(ulong memory)
        {
            RequireLive(memory, "vkFreeMemory");

            _liveHandles.Remove(memory);
            _mappedHandles.Remove(memory);

            if (_pinned.Remove(memory, out GCHandle pinned)) pinned.Free();

            FreeCount++;
        }

        /// <summary>The size of the chunk behind <paramref name="handle"/>, for a test that has to know what a
        /// widened range was clamped against.</summary>
        internal ulong SizeOf(ulong handle)
        {
            for (int i = 0; i < _allocations.Count; i++)
            {
                if (_allocations[i].Handle == handle) return _allocations[i].Size;
            }

            throw new InvalidOperationException("No allocation was made with that handle.");
        }

        void RequireLive(ulong memory, string call)
        {
            if (_liveHandles.Contains(memory)) return;

            throw new InvalidOperationException(
                $"{call} was called against VkDeviceMemory 0x{memory:x} which this fake never handed out or has "
                + "already freed. On a real device that is a call against freed memory, which aborts the process "
                + "through the Vulkan loader.");
        }
    }
}
