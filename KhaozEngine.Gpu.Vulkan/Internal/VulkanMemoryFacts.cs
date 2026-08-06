using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// The memory-type properties this allocator decides on, as its OWN flags rather than
    /// <c>VkMemoryPropertyFlags</c>. Section 9.1, decisions V-M1 to V-M4.
    /// <para>
    /// The translation happens once, in <see cref="VulkanPhysicalDeviceReader"/>, and everything downstream of it
    /// is plain data. That is the same split row 2 took with <see cref="VulkanDeviceFacts"/>: reading a physical
    /// device needs a loader, an instance and a driver, and DECIDING on what was read needs nothing, so the
    /// selection ladders in <see cref="VulkanMemoryTypeSelection"/> are driven from fabricated values on a machine
    /// with no Vulkan loader.
    /// </para>
    /// </summary>
    [Flags]
    internal enum VulkanMemoryTrait
    {
        /// <summary>No property at all, which is a legal memory type and the one a fallback rung matches.</summary>
        None = 0,

        /// <summary><c>VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT</c>. What every static resource wants and what an
        /// upload heap deliberately does not, because a staging buffer in device-local memory spends VRAM to hold
        /// bytes on their way somewhere else.</summary>
        DeviceLocal = 1 << 0,

        /// <summary><c>VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT</c>. The precondition for mapping at all, so every
        /// chunk this allocator maps carries it and no chunk without it is ever mapped.</summary>
        HostVisible = 1 << 1,

        /// <summary><c>VK_MEMORY_PROPERTY_HOST_COHERENT_BIT</c>. PREFERRED everywhere and REQUIRED for the uniform
        /// ring (V-M4). A chunk carrying it needs no <c>vkFlushMappedMemoryRanges</c> and no
        /// <c>vkInvalidateMappedMemoryRanges</c> at all, which is why the flush and invalidate paths below are
        /// documented as free rather than as cheap.</summary>
        HostCoherent = 1 << 2,

        /// <summary><c>VK_MEMORY_PROPERTY_HOST_CACHED_BIT</c>. Preferred for READBACK staging and nowhere else:
        /// uncached host memory reads at a small fraction of cached speed, and readback is the one direction the
        /// CPU reads a whole resource back. It is also the realistic way to end up on a NON-coherent type, which
        /// is what makes the invalidate path real code rather than a defensive branch.</summary>
        HostCached = 1 << 3,

        /// <summary><c>VK_MEMORY_PROPERTY_LAZILY_ALLOCATED_BIT</c>. Never selected, on any ladder: it can only
        /// back a transient attachment, has no host visibility, and a driver may commit nothing for it at all.
        /// Carried so the exclusion is a named property rather than a type that silently never matches.</summary>
        LazilyAllocated = 1 << 4,

        /// <summary><c>VK_MEMORY_PROPERTY_PROTECTED_BIT</c>. Never selected either: it is only usable from a
        /// protected-capable device and queue, which this backend does not create (V-N5).</summary>
        Protected = 1 << 5,
    }

    /// <summary>
    /// WHAT A CALLER IS ALLOCATING FOR, which is the only thing the type-selection ladders switch on. Section 9.1.
    /// <para>
    /// Deliberately four cases rather than a flags word of "I would like these bits". A caller that could hand the
    /// allocator a property mask would be deciding the policy at the call site, and the whole point of a ladder is
    /// that the fallback order is written down ONCE where it can be read and tested.
    /// </para>
    /// </summary>
    internal enum VulkanMemoryUsage
    {
        /// <summary>A resource the GPU reads and the CPU does not touch after upload: meshes, textures, render
        /// targets. Device-local, and preferably NOT host-visible, so it never lands in a small resizable-BAR
        /// window on a discrete card.</summary>
        DeviceLocal,

        /// <summary>Staging memory the CPU WRITES and the GPU reads once. Host-visible and coherent, and
        /// preferably not device-local.</summary>
        Upload,

        /// <summary>The per-frame uniform ring (V-M4, 9.2). Host-visible and <b>HOST_COHERENT</b> as a HARD
        /// requirement with no fallback rung at all, because 9.2's no-barrier argument rests on it and the
        /// alternative is a per-frame flush over every written segment, which is exactly the per-frame work the
        /// ring exists to remove. The support probe (row 2) already refuses a device reporting no such type, so
        /// this failing is a machine that changed between the probe and the call.</summary>
        Ring,

        /// <summary>Staging memory the CPU READS after the GPU has written it. Host-visible and CACHED preferred,
        /// which is the one place a non-coherent type is realistically chosen and therefore the one place
        /// <c>vkInvalidateMappedMemoryRanges</c> is real code.</summary>
        Readback,
    }

    /// <summary>
    /// The pool key's second half (V-M2). Buffers are <c>VK_IMAGE_TILING_LINEAR</c>-equivalent and optimal-tiling
    /// images are not, and the two may not share a <c>bufferImageGranularity</c> page.
    /// <para>
    /// <b>THIS ENUM IS THE ENTIRE GRANULARITY IMPLEMENTATION, and that is the point.</b> The incumbent rounds every
    /// non-dedicated request up to a multiple of <c>bufferImageGranularity</c> and shares chunks between the two,
    /// which is correct and wasteful, and its rounding adds a whole granule even when the size is already aligned.
    /// Separating the pools makes the constraint STRUCTURAL: a linear allocation and an optimal one can never be
    /// in the same <c>VkDeviceMemory</c>, so they can never share a page, so there is no arithmetic to get wrong.
    /// There is deliberately no <c>bufferImageGranularity</c> read anywhere in this package.
    /// </para>
    /// </summary>
    internal enum VulkanMemoryTiling
    {
        /// <summary>Buffers, and images created with <c>VK_IMAGE_TILING_LINEAR</c>.</summary>
        Linear,

        /// <summary>Images created with <c>VK_IMAGE_TILING_OPTIMAL</c>, which is every image this engine
        /// creates that is not a staging surface.</summary>
        Optimal,
    }

    /// <summary>One memory type as the allocator sees it: its index, its heap and its properties.</summary>
    /// <param name="Index">The type's own index, which is what <c>VkMemoryAllocateInfo.memoryTypeIndex</c> takes
    /// and what a resource's <c>memoryTypeBits</c> mask is indexed by.</param>
    /// <param name="HeapIndex">Which heap it draws from. Carried for the diagnostic rather than for the choice:
    /// two types on one heap compete for the same bytes, which is what a reader of an out-of-memory report needs
    /// to know.</param>
    /// <param name="Traits">Its properties, translated once out of <c>VkMemoryPropertyFlags</c>.</param>
    internal readonly record struct VulkanMemoryTypeInfo(uint Index, uint HeapIndex, VulkanMemoryTrait Traits)
    {
        /// <summary>Whether this type carries EVERY trait in <paramref name="traits"/>. An empty mask is
        /// vacuously true, which is what makes a fallback rung match anything.</summary>
        internal bool Has(VulkanMemoryTrait traits) => (Traits & traits) == traits;

        /// <summary>Whether this type carries ANY trait in <paramref name="traits"/>, which is how a rung's
        /// FORBIDDEN mask is applied.</summary>
        internal bool HasAny(VulkanMemoryTrait traits) => (Traits & traits) != 0;

        /// <summary>Mappable at all.</summary>
        internal bool HostVisible => Has(VulkanMemoryTrait.HostVisible);

        /// <summary>Needs no flush and no invalidate, ever.</summary>
        internal bool HostCoherent => Has(VulkanMemoryTrait.HostCoherent);
    }

    /// <summary>
    /// Everything about a device's MEMORY that the allocator needs and that only a real device can answer, as
    /// plain data with no Vulkan handle in it. The memory half of <see cref="VulkanPhysicalDeviceRead"/>.
    /// </summary>
    /// <param name="Types">Every memory type the device exposes, in index order, so
    /// <c>Types[i].Index == i</c>.</param>
    /// <param name="NonCoherentAtomSize">
    /// <c>VkPhysicalDeviceLimits.nonCoherentAtomSize</c>, in bytes, always a power of two and at least 1. It is
    /// the granularity <c>vkFlushMappedMemoryRanges</c> and <c>vkInvalidateMappedMemoryRanges</c> work in, and on
    /// this allocator it does DOUBLE duty: it also becomes the minimum sub-allocation alignment inside any
    /// host-visible NON-coherent chunk, so a rounded range can never reach into a neighbouring allocation. See
    /// <see cref="VulkanMappedRange"/> for why that matters more than it looks.
    /// </param>
    /// <param name="MaxAllocationCount">
    /// <c>VkPhysicalDeviceLimits.maxMemoryAllocationCount</c>: how many live <c>vkAllocateMemory</c> results this
    /// device permits at once. The bet MV6 is measured against, and the reason
    /// <see cref="VulkanMemoryAllocator.LiveDeviceAllocations"/> is counted at all. The spec's required minimum is
    /// 4096 and real drivers report exactly that far more often than not.
    /// </param>
    internal readonly record struct VulkanMemoryFacts(
        IReadOnlyList<VulkanMemoryTypeInfo> Types,
        ulong NonCoherentAtomSize,
        uint MaxAllocationCount)
    {
        /// <summary>What a device with nothing readable looks like: no types, an atom size of 1 (which makes
        /// every range rounding an identity) and no allocation budget. Never produced by a real read.</summary>
        internal static VulkanMemoryFacts Empty { get; } = new(Array.Empty<VulkanMemoryTypeInfo>(), 1, 0);

        /// <summary>V-M4's read, over the translated types rather than over the driver's struct a second time:
        /// whether ANY type is both host-visible and host-coherent. The uniform ring is pinned to one and the
        /// support probe refuses a device reporting none.</summary>
        internal bool HasCoherentHostVisibleType
        {
            get
            {
                const VulkanMemoryTrait required = VulkanMemoryTrait.HostVisible | VulkanMemoryTrait.HostCoherent;

                for (int i = 0; i < Types.Count; i++)
                {
                    if (Types[i].Has(required)) return true;
                }
                return false;
            }
        }
    }
}
