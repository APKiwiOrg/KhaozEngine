using System;
using System.Globalization;
using KhaozEngine.Gpu.Internal;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE FIVE REAL DRIVER CALLS BEHIND <see cref="IVulkanDeviceMemoryApi"/>, and nothing else. Everything that
    /// decides anything is above this line, in <see cref="VulkanMemoryAllocator"/>,
    /// <see cref="VulkanMemoryChunk"/>, <see cref="VulkanMemoryFreeList"/> and
    /// <see cref="VulkanMemoryTypeSelection"/>, which is what makes all of it testable with no loader.
    /// <para>
    /// EVERY RESULT-RETURNING CALL GOES THROUGH THE LOSS LATCH FIRST AND THEN THROUGH
    /// <see cref="VulkanResultCodes.Require"/>, in every configuration, with no discarded results anywhere. The
    /// spec names <c>vkAllocateMemory</c> and <c>vkMapMemory</c> among the calls that can return
    /// <c>VK_ERROR_DEVICE_LOST</c>, and the incumbent's <c>VulkanUtil.CheckResult</c> is
    /// <c>[Conditional("DEBUG")]</c>, so a Release build of it takes a lost device back from an allocation and
    /// carries on with a handle that is not one. <c>vkFreeMemory</c> returns nothing and is the only member here
    /// with no result to check.
    /// </para>
    /// <para>
    /// <c>vkFreeMemory</c> IS SKIPPED ON A DEAD DEVICE, through the same liveness token every other destroy in
    /// this package is gated on. <c>vkDestroyDevice</c> (or the loss that killed it) already freed every object
    /// made from the device, so a free afterwards is a call against freed memory, which aborts the process through
    /// the Vulkan loader rather than failing quietly.
    /// </para>
    /// </summary>
    internal sealed unsafe class VulkanDeviceMemoryApi : IVulkanDeviceMemoryApi
    {
        readonly Vk _vk;
        readonly Device _device;
        readonly VulkanDeviceLossLatch _loss;
        readonly IDeviceLiveness _liveness;

        /// <param name="vk">The instance's loaded API.</param>
        /// <param name="device">The device that owns every allocation made here and outlives them all.</param>
        /// <param name="loss">The device's loss latch, which every result here is checked against.</param>
        /// <param name="liveness">The device's liveness token, which gates the free.</param>
        internal VulkanDeviceMemoryApi(Vk vk, Device device, VulkanDeviceLossLatch loss,
            IDeviceLiveness liveness)
        {
            ArgumentNullException.ThrowIfNull(vk);
            ArgumentNullException.ThrowIfNull(loss);
            ArgumentNullException.ThrowIfNull(liveness);

            _vk = vk;
            _device = device;
            _loss = loss;
            _liveness = liveness;
        }

        /// <inheritdoc/>
        public ulong Allocate(uint memoryTypeIndex, ulong size, VulkanDedicatedTarget dedicated)
        {
            var dedicatedInfo = new MemoryDedicatedAllocateInfo(
                sType: StructureType.MemoryDedicatedAllocateInfo,
                image: new Image(dedicated.Image),
                buffer: new Buffer(dedicated.Buffer));

            // The chain is present only when there IS a target. A VkMemoryDedicatedAllocateInfo naming neither a
            // buffer nor an image is legal and means nothing, but chaining an empty structure onto every
            // allocation would make a reader of a capture believe every allocation was dedicated.
            var allocateInfo = new MemoryAllocateInfo(
                sType: StructureType.MemoryAllocateInfo,
                pNext: dedicated.IsSet ? &dedicatedInfo : null,
                allocationSize: size,
                memoryTypeIndex: memoryTypeIndex);

            Result allocated = _vk.AllocateMemory(_device, in allocateInfo, null, out DeviceMemory memory);

            // THE LATCH FIRST, so the site's own name is what the telemetry header carries.
            if (_loss.Check(allocated, "vkAllocateMemory"))
            {
                throw new InvalidOperationException(
                    "The native Vulkan backend could not allocate device memory, because the device was LOST. The "
                    + "loss itself is in the session log and in the telemetry session header, with the call that "
                    + "first noticed it.");
            }

            VulkanResultCodes.Require(allocated, "vkAllocateMemory");
            return memory.Handle;
        }

        /// <inheritdoc/>
        public nint MapWhole(ulong memory, ulong size)
        {
            void* mapped = null;

            // VK_WHOLE_SIZE from offset 0, which is the mapping this backend takes and the only one it takes
            // (V-M3). The size parameter above is for the diagnostic below rather than for the call.
            Result result = _vk.MapMemory(_device, new DeviceMemory(memory), 0, Vk.WholeSize, 0, &mapped);

            if (_loss.Check(result, "vkMapMemory (whole chunk, persistent)"))
            {
                throw new InvalidOperationException(
                    "The native Vulkan backend could not map a host-visible memory chunk, because the device was "
                    + "LOST. Host-visible chunks are mapped once at creation and never unmapped, so there is no "
                    + "later attempt to fall back to.");
            }

            VulkanResultCodes.Require(result, "vkMapMemory");

            if (mapped is null)
            {
                throw new InvalidOperationException(
                    "vkMapMemory reported success and handed back a null pointer for a native Vulkan memory chunk "
                    + "of " + size.ToString(CultureInfo.InvariantCulture) + " bytes. That is a driver "
                    + "contradicting itself, and every suballocation out of this chunk would carry a null base.");
            }

            return (nint)mapped;
        }

        /// <inheritdoc/>
        public void Flush(ulong memory, ulong offset, ulong size)
            => Range(memory, offset, size, invalidate: false);

        /// <inheritdoc/>
        public void Invalidate(ulong memory, ulong offset, ulong size)
            => Range(memory, offset, size, invalidate: true);

        /// <inheritdoc/>
        public void Free(ulong memory)
        {
            // The liveness gate, not the loss latch: this call returns nothing, so there is no result to check,
            // and the only question is whether the object still exists to be freed.
            if (_liveness.IsDead) return;

            _vk.FreeMemory(_device, new DeviceMemory(memory), null);
        }

        // vkFlushMappedMemoryRanges and vkInvalidateMappedMemoryRanges take the same structure and differ only in
        // direction, so one body with a flag beats two that can drift. The range arrives already widened to
        // nonCoherentAtomSize by VulkanMappedRange, which is where the spec's alignment rules are enforced.
        void Range(ulong memory, ulong offset, ulong size, bool invalidate)
        {
            var range = new MappedMemoryRange(
                sType: StructureType.MappedMemoryRange,
                memory: new DeviceMemory(memory),
                offset: offset,
                size: size);

            Result result = invalidate
                ? _vk.InvalidateMappedMemoryRanges(_device, 1, in range)
                : _vk.FlushMappedMemoryRanges(_device, 1, in range);

            string site = invalidate ? "vkInvalidateMappedMemoryRanges" : "vkFlushMappedMemoryRanges";

            if (_loss.Check(result, site))
            {
                throw new InvalidOperationException(
                    $"The native Vulkan backend's {site} failed because the device was LOST. The bytes it was "
                    + "making available in one direction or the other are not, and the work that depended on them "
                    + "will never run.");
            }

            VulkanResultCodes.Require(result, site);
        }
    }
}
