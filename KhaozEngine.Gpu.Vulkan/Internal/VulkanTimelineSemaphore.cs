using System;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE ONE REAL <c>VkSemaphore</c> OF TYPE TIMELINE, initial value 0, created with the device and destroyed
    /// before it (V-F1). Everything here is a native call and nothing here decides anything, which is the split
    /// <see cref="IVulkanTimelineSemaphore"/> exists to make.
    /// <para>
    /// EVERY RESULT-RETURNING CALL GOES THROUGH THE LOSS LATCH FIRST AND THEN THROUGH
    /// <see cref="VulkanResultCodes.Require"/>, in every configuration, with no discarded results anywhere. That
    /// is not a house style: the incumbent's <c>VulkanUtil.CheckResult</c> is <c>[Conditional("DEBUG")]</c>, so a
    /// Release build of it can take <c>VK_ERROR_DEVICE_LOST</c> back from <c>vkWaitSemaphores</c> and carry on as
    /// though the wait succeeded. <c>vkGetSemaphoreCounterValue</c> and <c>vkWaitSemaphores</c> are both named in
    /// the spec as able to return it, and both are wired here with their own site names, which is what "latched at
    /// the fault site" means.
    /// </para>
    /// <para>
    /// THE HANDLE IS EXPOSED because row 7 (https://github.com/APKiwiOrg/KhaozEngine/issues/517) has to name it in
    /// the <c>VkTimelineSemaphoreSubmitInfo</c> chained onto every <c>vkQueueSubmit</c>, and row 8 has to name it
    /// where the ring's segment gate reads a completion value. It is exposed by the CONCRETE type rather than by
    /// the interface, so the fake a test builds never has to invent a handle it cannot have.
    /// </para>
    /// </summary>
    internal sealed unsafe class VulkanTimelineSemaphore : IVulkanTimelineSemaphore
    {
        readonly Vk _vk;
        readonly Device _device;
        readonly VulkanDeviceLossLatch _loss;
        readonly Semaphore _handle;

        bool _disposed;

        /// <summary>
        /// Create the device's timeline semaphore. Throws when creation fails, which at this point in a device's
        /// life is the creation-time failure <see cref="VulkanResultCodes.Require"/> describes: the caller
        /// destroys the half-built device rather than running on one with no completion signal.
        /// </summary>
        /// <param name="vk">The instance's loaded API.</param>
        /// <param name="device">The device that owns this semaphore and outlives it.</param>
        /// <param name="loss">The device's loss latch, which every result here is checked against.</param>
        internal VulkanTimelineSemaphore(Vk vk, Device device, VulkanDeviceLossLatch loss)
        {
            ArgumentNullException.ThrowIfNull(vk);
            ArgumentNullException.ThrowIfNull(loss);

            _vk = vk;
            _device = device;
            _loss = loss;

            // INITIAL VALUE 0, which is what makes 0 usable as the "unarmed" marker on a fence: the first value
            // any submission can signal is 1, so no real target can collide with it.
            var typeInfo = new SemaphoreTypeCreateInfo(
                sType: StructureType.SemaphoreTypeCreateInfo,
                semaphoreType: SemaphoreType.Timeline,
                initialValue: 0);

            var createInfo = new SemaphoreCreateInfo(
                sType: StructureType.SemaphoreCreateInfo, pNext: &typeInfo);

            VulkanResultCodes.Require(
                _vk.CreateSemaphore(_device, in createInfo, null, out Semaphore handle),
                "vkCreateSemaphore (the device timeline)");

            _handle = handle;
        }

        /// <summary>The raw handle, for the submit path that signals on it and the ring that gates on it. See the
        /// class note for why it is here and not on the interface.</summary>
        internal Semaphore Handle => _handle;

        /// <inheritdoc/>
        public ulong Read()
        {
            Result read = _vk.GetSemaphoreCounterValue(_device, _handle, out ulong value);

            // The latch FIRST, so the site's own name is what the telemetry header carries. A lost device makes
            // the value meaningless, and 0 is the conservative thing to hand back: the caller re-reads liveness
            // and answers from what it issued instead, so this number is never the one that decides anything.
            if (_loss.Check(read, "vkGetSemaphoreCounterValue (the device timeline)")) return 0;

            VulkanResultCodes.Require(read, "vkGetSemaphoreCounterValue");
            return value;
        }

        /// <inheritdoc/>
        public bool WaitUntil(ulong value)
        {
            // Locals rather than the fields, because the wait info takes their addresses and a readonly field is
            // not a legal target for that.
            Semaphore handle = _handle;
            ulong target = value;

            var waitInfo = new SemaphoreWaitInfo(
                sType: StructureType.SemaphoreWaitInfo,
                semaphoreCount: 1,
                pSemaphores: &handle,
                pValues: &target);

            Result waited = _vk.WaitSemaphores(_device, in waitInfo, ulong.MaxValue);
            if (_loss.Check(waited, "vkWaitSemaphores (WaitForIdle)")) return false;

            VulkanResultCodes.Require(waited, "vkWaitSemaphores");

            // VK_TIMEOUT cannot come back from an infinite wait, and it is a SUCCESS code rather than a failure,
            // so Require above would let it through. Comparing against VK_SUCCESS rather than assuming is what
            // keeps a driver that returns one from reading as a completed wait.
            return waited == Result.Success;
        }

        /// <summary>
        /// Destroy the semaphore. Called by <see cref="VulkanTimeline.Dispose"/>, which is gated on device
        /// liveness, so this never runs against a device that has already gone.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _vk.DestroySemaphore(_device, _handle, null);
        }
    }
}
