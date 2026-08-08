using System;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE SEVEN REAL DRIVER CALLS BEHIND <see cref="IVulkanCommandApi"/>, and nothing else. Everything that
    /// decides anything is above this line, in <see cref="VulkanCommandPoolRing"/>,
    /// <see cref="VulkanCommandList"/> and <see cref="VulkanSubmitQueue"/>, which is what makes the slot advance,
    /// the wrap, the recording state machine and the whole submit ordering testable with no loader.
    /// <para>
    /// EVERY RESULT-RETURNING CALL GOES THROUGH THE LOSS LATCH FIRST, in every configuration, with no discarded
    /// results anywhere. <c>vkQueueSubmit</c> is the site the spec names first among those that can return
    /// <c>VK_ERROR_DEVICE_LOST</c>, and the incumbent's <c>VulkanUtil.CheckResult</c> is
    /// <c>[Conditional("DEBUG")]</c>, so a Release build of it takes a lost device back from a submit and carries
    /// on as though the frame had been queued.
    /// </para>
    /// <para>
    /// <c>vkDestroyCommandPool</c> IS SKIPPED ON A DEAD DEVICE, through the same liveness token every other
    /// destroy in this package is gated on, and it is the only member here with no result to check.
    /// </para>
    /// <para>
    /// THE SUBMIT DOES NOT THROW, unlike the memory seam's allocate. It reports which of the three things
    /// happened and lets <see cref="VulkanSubmitQueue"/> decide, because the decision is about the TIMELINE (does
    /// the value this submission took get registered as one the GPU will reach) and the timeline is not visible
    /// from down here. A throw at this line would take the submit lock's bookkeeping with it.
    /// </para>
    /// <para>
    /// <see cref="ResetPool"/> AND <see cref="BeginOneTimeSubmit"/> CARRY A DELIBERATE LIVENESS-GATE ASYMMETRY
    /// against <see cref="DestroyPool"/>. A LOST device reaching either of them is safe with no explicit gate,
    /// because the loss latch's <c>Check</c> returns quietly on a device already latched lost. A DESTROYED device
    /// reaching either of them is caller misuse instead, the same as a use-after-dispose call anywhere else in
    /// this package, which is why only <see cref="DestroyPool"/> carries the explicit <c>_liveness.IsDead</c>
    /// gate: it is the one call here with a native result too weak to catch that case on its own.
    /// </para>
    /// </summary>
    internal sealed unsafe class VulkanCommandApi : IVulkanCommandApi
    {
        readonly Vk _vk;
        readonly Device _device;
        readonly Queue _queue;
        readonly uint _queueFamily;
        readonly Semaphore _timeline;
        readonly VulkanDeviceLossLatch _loss;
        readonly IVulkanDeviceLiveness _liveness;

        /// <param name="vk">The instance's loaded API.</param>
        /// <param name="device">The device that owns every pool made here and outlives them all.</param>
        /// <param name="queue">The device's ONE graphics queue (V-N5), which every submission goes through.</param>
        /// <param name="queueFamily">The family that queue came from, which pools are created against.</param>
        /// <param name="timeline">The device's ONE timeline semaphore, signalled by every submission.</param>
        /// <param name="loss">The device's loss latch, which every result here is checked against.</param>
        /// <param name="liveness">The device's liveness token, which gates the destroy.</param>
        internal VulkanCommandApi(Vk vk, Device device, Queue queue, uint queueFamily, Semaphore timeline,
            VulkanDeviceLossLatch loss, IVulkanDeviceLiveness liveness)
        {
            ArgumentNullException.ThrowIfNull(vk);
            ArgumentNullException.ThrowIfNull(loss);
            ArgumentNullException.ThrowIfNull(liveness);

            _vk = vk;
            _device = device;
            _queue = queue;
            _queueFamily = queueFamily;
            _timeline = timeline;
            _loss = loss;
            _liveness = liveness;
        }

        /// <inheritdoc/>
        public ulong CreatePool()
        {
            // NO FLAGS AT ALL. Not RESET_COMMAND_BUFFER, which is the decision V-R2 took and which the seam has no
            // parameter for, and not TRANSIENT either: this pool is reset and refilled on a fixed slot cadence
            // rather than being a short-lived one-shot, and adding a driver hint the design did not weigh would
            // change the allocator strategy underneath a measurement nobody has taken yet.
            var createInfo = new CommandPoolCreateInfo(
                sType: StructureType.CommandPoolCreateInfo,
                flags: CommandPoolCreateFlags.None,
                queueFamilyIndex: _queueFamily);

            Result created = _vk.CreateCommandPool(_device, in createInfo, null, out CommandPool pool);

            if (_loss.Check(created, "vkCreateCommandPool"))
            {
                throw new InvalidOperationException(
                    "The native Vulkan backend could not create a command pool, because the device was LOST. The "
                    + "loss itself is in the session log and in the telemetry session header, with the call that "
                    + "first noticed it.");
            }

            VulkanResultCodes.Require(created, "vkCreateCommandPool");
            return pool.Handle;
        }

        /// <inheritdoc/>
        public ulong AllocatePrimaryBuffer(ulong pool)
        {
            var allocateInfo = new CommandBufferAllocateInfo(
                sType: StructureType.CommandBufferAllocateInfo,
                commandPool: new CommandPool(pool),
                level: CommandBufferLevel.Primary,
                commandBufferCount: 1);

            Result allocated = _vk.AllocateCommandBuffers(_device, in allocateInfo, out CommandBuffer buffer);

            if (_loss.Check(allocated, "vkAllocateCommandBuffers"))
            {
                throw new InvalidOperationException(
                    "The native Vulkan backend could not allocate a command buffer, because the device was LOST. "
                    + "The loss itself is in the session log and in the telemetry session header, with the call "
                    + "that first noticed it.");
            }

            VulkanResultCodes.Require(allocated, "vkAllocateCommandBuffers");

            // A DISPATCHABLE handle, so it is a pointer rather than a 64-bit integer on the native side. The
            // conversion happens at this line and nowhere above it, which is what lets every type over this seam
            // hold a plain ulong and lets a fake invent one.
            return (ulong)buffer.Handle;
        }

        /// <inheritdoc/>
        public void ResetPool(ulong pool)
        {
            // NO RELEASE_RESOURCES. The memory the last record used stays in the pool's arena for the next one,
            // which is the whole reason a pool reset is the fast path this design chose over per-buffer resets.
            Result reset = _vk.ResetCommandPool(_device, new CommandPool(pool), CommandPoolResetFlags.None);

            if (_loss.Check(reset, "vkResetCommandPool")) return;

            VulkanResultCodes.Require(reset, "vkResetCommandPool");
        }

        /// <inheritdoc/>
        public void BeginOneTimeSubmit(ulong commandBuffer)
        {
            var beginInfo = new CommandBufferBeginInfo(
                sType: StructureType.CommandBufferBeginInfo,
                flags: CommandBufferUsageFlags.OneTimeSubmitBit);

            Result begun = _vk.BeginCommandBuffer(Buffer(commandBuffer), in beginInfo);

            if (_loss.Check(begun, "vkBeginCommandBuffer")) return;

            VulkanResultCodes.Require(begun, "vkBeginCommandBuffer");
        }

        /// <inheritdoc/>
        public void EndRecording(ulong commandBuffer)
        {
            Result ended = _vk.EndCommandBuffer(Buffer(commandBuffer));

            if (_loss.Check(ended, "vkEndCommandBuffer")) return;

            VulkanResultCodes.Require(ended, "vkEndCommandBuffer");
        }

        /// <inheritdoc/>
        public VulkanSubmitStatus Submit(ulong commandBuffer, ulong signalValue,
            in VulkanFrameSemaphores frame, out string? failure)
        {
            failure = null;

            CommandBuffer buffer = Buffer(commandBuffer);
            ulong value = signalValue;

            // THE SIGNAL ARRAYS ARE POSITIONAL AGAINST EACH OTHER, which is the whole shape of mixing a timeline
            // semaphore with a binary one in one submit. The value array has an entry per signal semaphore, and
            // the entry for a BINARY semaphore is ignored, so the timeline's value goes first and the swapchain's
            // render-finished semaphore takes a zero it never reads. Getting the two arrays out of step is a
            // submit that signals the timeline at the wrong value, which is silent until a drain hangs.
            Semaphore* signals = stackalloc Semaphore[2];
            ulong* signalValues = stackalloc ulong[2];
            signals[0] = _timeline;
            signalValues[0] = value;

            uint signalCount = 1;
            if (frame.Signal != 0)
            {
                signals[1] = new Semaphore(frame.Signal);
                signalValues[1] = 0;
                signalCount = 2;
            }

            // THE WAIT IS THE ACQUIRE SEMAPHORE AT COLOR_ATTACHMENT_OUTPUT (V-W3), and that stage is the decision
            // rather than a detail: everything before the colour write may run before the image is available, so
            // waiting at TOP_OF_PIPE would serialise the whole frame behind the presentation engine for no reason.
            Semaphore wait = new(frame.Wait);
            PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit;

            var values = new TimelineSemaphoreSubmitInfo(
                sType: StructureType.TimelineSemaphoreSubmitInfo,
                signalSemaphoreValueCount: signalCount,
                pSignalSemaphoreValues: signalValues);

            var submitInfo = new SubmitInfo(
                sType: StructureType.SubmitInfo,
                pNext: &values,
                waitSemaphoreCount: frame.Wait == 0 ? 0u : 1u,
                pWaitSemaphores: frame.Wait == 0 ? null : &wait,
                pWaitDstStageMask: frame.Wait == 0 ? null : &waitStage,
                commandBufferCount: 1,
                pCommandBuffers: &buffer,
                signalSemaphoreCount: signalCount,
                pSignalSemaphores: signals);

            // ONE vkQueueSubmit (V-F3), and no VkFence: this backend has no VkFence anywhere, because the one
            // timeline is what every completion question is answered against.
            Result submitted = _vk.QueueSubmit(_queue, 1, in submitInfo, default);

            if (_loss.Check(submitted, "vkQueueSubmit")) return VulkanSubmitStatus.DeviceLost;
            if (!VulkanResultCodes.IsFailure(submitted)) return VulkanSubmitStatus.Success;

            failure = VulkanResultCodes.Token(submitted);
            return VulkanSubmitStatus.Failed;
        }

        /// <inheritdoc/>
        public void DestroyPool(ulong pool)
        {
            if (_liveness.IsDead) return;

            _vk.DestroyCommandPool(_device, new CommandPool(pool), null);
        }

        static CommandBuffer Buffer(ulong handle) => new((nint)handle);
    }
}
