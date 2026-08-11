using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Internal;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE REAL DRIVER CALLS BEHIND <see cref="IVulkanSwapchainApi"/>, and nothing else. The acquire ring's
    /// indexing, the <c>OUT_OF_DATE</c> state machine, the retry count, the retirement order and the acquire-wait
    /// counter all sit above this line, which is what makes them decidable under <c>dotnet test</c> on a machine
    /// with no Vulkan loader and no window (MV9).
    ///
    /// <para><b>EVERY RESULT IS MAPPED RATHER THAN CHECKED-AND-DISCARDED (V-W7).</b> The incumbent ignores
    /// <c>vkQueuePresentKHR</c>'s result entirely, so it can never learn that the surface it presents to changed
    /// underneath it, and it treats <c>VK_SUBOPTIMAL_KHR</c> as a plain success because Vulkan's success codes are
    /// zero and positive. Both distinctions are the whole content of the boundary above, so they arrive there as
    /// separate outcomes rather than as a <c>VkResult</c> somebody has to remember to read correctly.</para>
    ///
    /// <para><b>THE ONE <c>VkFence</c> IN THIS BACKEND LIVES HERE, and only for the kill switch.</b> The
    /// completion model is one timeline semaphore (V-F1) and there is no <c>VkFence</c> anywhere else. The stall
    /// mode reproduces the incumbent's acquire exactly, which means a fence and a blocking
    /// <c>vkWaitForFences</c>, so the fence is created lazily on the first stalling acquire, reset between uses
    /// and destroyed with this object. Nothing above this line knows it exists, which is what keeps the removal
    /// of the switch at rollout gate 4 a deletion of one path rather than an unpicking.</para>
    /// </summary>
    internal sealed unsafe class VulkanSwapchainApi : IVulkanSwapchainApi, IDisposable
    {
        readonly Vk _vk;
        readonly Device _device;
        readonly Queue _queue;
        readonly KhrSwapchain _swapchain;
        readonly VulkanDeviceLossLatch _loss;
        readonly IDeviceLiveness _liveness;

        Fence _acquireFence;
        bool _disposed;

        /// <param name="vk">The instance's loaded API.</param>
        /// <param name="instance">The shared instance, needed to resolve the per-device swapchain entry points.</param>
        /// <param name="device">The device every object here is a child of.</param>
        /// <param name="queue">The device's ONE graphics queue, which also presents (V-N5).</param>
        /// <param name="loss">The device's loss latch, which every result here is checked against.</param>
        /// <param name="liveness">The device's liveness token, which gates every destroy.</param>
        /// <exception cref="NotSupportedException">The device carries no <c>VK_KHR_swapchain</c>, which means a
        /// headless device was handed to a windowed path.</exception>
        internal VulkanSwapchainApi(Vk vk, Instance instance, Device device, Queue queue,
            VulkanDeviceLossLatch loss, IDeviceLiveness liveness)
        {
            ArgumentNullException.ThrowIfNull(vk);
            ArgumentNullException.ThrowIfNull(loss);
            ArgumentNullException.ThrowIfNull(liveness);

            _vk = vk;
            _device = device;
            _queue = queue;
            _loss = loss;
            _liveness = liveness;

            if (!vk.TryGetDeviceExtension(instance, device, out KhrSwapchain swapchain, KhrSwapchain.ExtensionName))
            {
                throw new NotSupportedException(
                    "The native Vulkan backend could not load VK_KHR_swapchain from its device. The headless path "
                    + "enables no device extension at all (V-N6), so this is a headless device being asked to "
                    + "present. Windowed devices are created with the extension from the start.");
            }

            _swapchain = swapchain;
        }

        /// <inheritdoc/>
        public ulong CreateSwapchain(ulong surface, in VulkanSwapchainSpec spec, ulong oldSwapchain,
            out string? failure)
        {
            var info = new SwapchainCreateInfoKHR(
                sType: StructureType.SwapchainCreateInfoKhr,
                surface: new SurfaceKHR(surface),
                minImageCount: spec.ImageCount,
                imageFormat: spec.Format,
                imageColorSpace: spec.ColourSpace,
                imageExtent: new Extent2D(spec.Extent.Width, spec.Extent.Height),
                // ONE LAYER. No stereo and no multiview on the swapchain, matching the incumbent.
                imageArrayLayers: 1,
                imageUsage: spec.Usage,
                // EXCLUSIVE, which follows from V-N5's one queue family: there is nothing to share the images
                // with, and CONCURRENT on a single family is a slower mode for no reason.
                imageSharingMode: SharingMode.Exclusive,
                preTransform: spec.PreTransform,
                compositeAlpha: spec.CompositeAlpha,
                presentMode: spec.PresentMode,
                clipped: spec.Clipped,
                oldSwapchain: new SwapchainKHR(oldSwapchain));

            Result result = _swapchain.CreateSwapchain(_device, in info, null, out SwapchainKHR handle);
            if (_loss.Check(result, "vkCreateSwapchainKHR"))
            {
                failure = VulkanResultCodes.Token(result);
                return 0;
            }

            if (VulkanResultCodes.IsFailure(result))
            {
                failure = VulkanResultCodes.Describe(result);
                return 0;
            }

            failure = null;
            return handle.Handle;
        }

        /// <inheritdoc/>
        public IReadOnlyList<ulong> GetImages(ulong swapchain)
        {
            var handle = new SwapchainKHR(swapchain);

            uint count = 0;
            Result counted = _swapchain.GetSwapchainImages(_device, handle, ref count, null);
            if (_loss.Check(counted, "vkGetSwapchainImagesKHR") || VulkanResultCodes.IsFailure(counted)
                || count == 0)
            {
                return Array.Empty<ulong>();
            }

            var images = new Image[count];
            fixed (Image* buffer = images)
            {
                Result filled = _swapchain.GetSwapchainImages(_device, handle, ref count, buffer);
                if (_loss.Check(filled, "vkGetSwapchainImagesKHR") || VulkanResultCodes.IsFailure(filled))
                    return Array.Empty<ulong>();
            }

            var handles = new ulong[count];
            for (uint i = 0; i < count; i++) handles[i] = images[i].Handle;
            return handles;
        }

        /// <inheritdoc/>
        public void DestroySwapchain(ulong swapchain)
        {
            if (swapchain == 0 || _liveness.IsDead) return;

            _swapchain.DestroySwapchain(_device, new SwapchainKHR(swapchain), null);
        }

        /// <inheritdoc/>
        public ulong CreateImageView(ulong image, Format format)
        {
            var info = new ImageViewCreateInfo(
                sType: StructureType.ImageViewCreateInfo,
                image: new Image(image),
                viewType: ImageViewType.Type2D,
                format: format,
                subresourceRange: new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1));

            Result result = _vk.CreateImageView(_device, in info, null, out ImageView view);
            _loss.Check(result, "vkCreateImageView (swapchain image)");
            VulkanResultCodes.Require(result, "vkCreateImageView (swapchain image)");
            return view.Handle;
        }

        /// <inheritdoc/>
        public void DestroyImageView(ulong view)
        {
            if (view == 0 || _liveness.IsDead) return;

            _vk.DestroyImageView(_device, new ImageView(view), null);
        }

        /// <inheritdoc/>
        public ulong CreateBinarySemaphore()
        {
            // NO SemaphoreTypeCreateInfo CHAINED ON, which is what makes this a BINARY semaphore. The device's own
            // completion semaphore is a TIMELINE one and is created with that structure chained (V-F1). Those two
            // are the only kinds this backend has, and VK_KHR_swapchain accepts only this one.
            var info = new SemaphoreCreateInfo(sType: StructureType.SemaphoreCreateInfo);

            Result result = _vk.CreateSemaphore(_device, in info, null, out Semaphore semaphore);
            _loss.Check(result, "vkCreateSemaphore (swapchain binary)");
            VulkanResultCodes.Require(result, "vkCreateSemaphore (swapchain binary)");
            return semaphore.Handle;
        }

        /// <inheritdoc/>
        public void DestroySemaphore(ulong semaphore)
        {
            if (semaphore == 0 || _liveness.IsDead) return;

            _vk.DestroySemaphore(_device, new Semaphore(semaphore), null);
        }

        /// <inheritdoc/>
        public VulkanPresentOutcome AcquireNextImage(ulong swapchain, ulong semaphore, bool blockUntilReady,
            out uint imageIndex)
        {
            uint index = 0;
            Result result = _swapchain.AcquireNextImage(
                _device,
                new SwapchainKHR(swapchain),
                blockUntilReady ? ulong.MaxValue : 0,
                new Semaphore(semaphore),
                default,
                &index);

            imageIndex = index;
            return Interpret(result, "vkAcquireNextImageKHR");
        }

        /// <inheritdoc/>
        public VulkanPresentOutcome AcquireNextImageStalling(ulong swapchain, out uint imageIndex)
        {
            imageIndex = 0;
            if (_acquireFence.Handle == 0)
            {
                var fenceInfo = new FenceCreateInfo(sType: StructureType.FenceCreateInfo);
                Result created = _vk.CreateFence(_device, in fenceInfo, null, out Fence fence);
                _loss.Check(created, "vkCreateFence (stall-mode acquire)");
                VulkanResultCodes.Require(created, "vkCreateFence (stall-mode acquire)");
                _acquireFence = fence;
            }

            uint index = 0;
            Result acquired = _swapchain.AcquireNextImage(
                _device, new SwapchainKHR(swapchain), ulong.MaxValue, default, _acquireFence, &index);

            VulkanPresentOutcome outcome = Interpret(acquired, "vkAcquireNextImageKHR (stall)");
            if (outcome is not (VulkanPresentOutcome.Success or VulkanPresentOutcome.Suboptimal)) return outcome;

            // THE BLOCK ITSELF, with an infinite timeout, which is the incumbent's shape exactly and the whole of
            // what the semaphore path removes.
            Fence fenceHandle = _acquireFence;
            Result waited = _vk.WaitForFences(_device, 1, in fenceHandle, true, ulong.MaxValue);
            if (_loss.Check(waited, "vkWaitForFences (stall-mode acquire)")) return VulkanPresentOutcome.DeviceLost;

            Result reset = _vk.ResetFences(_device, 1, in fenceHandle);
            if (_loss.Check(reset, "vkResetFences (stall-mode acquire)")) return VulkanPresentOutcome.DeviceLost;

            imageIndex = index;
            return outcome;
        }

        /// <inheritdoc/>
        public VulkanPresentOutcome Present(ulong swapchain, uint imageIndex, ulong waitSemaphore)
        {
            var chain = new SwapchainKHR(swapchain);
            var wait = new Semaphore(waitSemaphore);
            uint index = imageIndex;

            var info = new PresentInfoKHR(
                sType: StructureType.PresentInfoKhr,
                waitSemaphoreCount: waitSemaphore == 0 ? 0u : 1u,
                pWaitSemaphores: waitSemaphore == 0 ? null : &wait,
                swapchainCount: 1,
                pSwapchains: &chain,
                pImageIndices: &index);

            return Interpret(_swapchain.QueuePresent(_queue, in info), "vkQueuePresentKHR");
        }

        /// <summary>Destroy the stall mode's fence, if one was ever made. Every other object this type creates
        /// belongs to a swapchain generation and is destroyed with it.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_acquireFence.Handle == 0 || _liveness.IsDead) return;

            _vk.DestroyFence(_device, _acquireFence, null);
            _acquireFence = default;
        }

        VulkanPresentOutcome Interpret(Result result, string call)
        {
            // THE LATCH FIRST, IN EVERY CONFIGURATION (V-G4), so a device loss is recorded with this call's own
            // name before anything downstream tries to reason about it.
            if (_loss.Check(result, call)) return VulkanPresentOutcome.DeviceLost;

            return result switch
            {
                Result.Success => VulkanPresentOutcome.Success,
                Result.SuboptimalKhr => VulkanPresentOutcome.Suboptimal,
                Result.ErrorOutOfDateKhr => VulkanPresentOutcome.OutOfDate,
                Result.NotReady or Result.Timeout => VulkanPresentOutcome.NotReady,
                Result.ErrorSurfaceLostKhr => VulkanPresentOutcome.SurfaceLost,
                _ => VulkanPresentOutcome.Failed,
            };
        }
    }
}
