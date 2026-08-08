using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// ONE GENERATION OF THE SWAPCHAIN: the <c>VkSwapchainKHR</c>, its images, one <c>VkImageView</c> per image
    /// and one RENDER-FINISHED binary semaphore per image, created together and destroyed together.
    ///
    /// <para><b>GENERATION IS THE RIGHT NOUN BECAUSE A RECREATE REPLACES EVERY OBJECT IN HERE.</b> Vulkan cannot
    /// resize a swapchain and cannot change its present mode in place, so both are a full recreate, and there is
    /// no partial update to express. Making the whole set one object with one lifetime is what keeps the
    /// destruction order in one place instead of spread over a resize path that has to remember four
    /// arrays.</para>
    ///
    /// <para><b>ONE RENDER-FINISHED SEMAPHORE PER IMAGE, NOT PER FRAME (V-F5).</b> It is signalled by the submit
    /// that renders into that image and waited by the present of that image, so it is a property of the image
    /// rather than of the frame. Per-frame would let a present of image 2 wait on a semaphore the submit for
    /// image 0 signalled, which is a present that runs before its own rendering finished.</para>
    ///
    /// <para><b>THE ACQUIRE SEMAPHORES ARE DELIBERATELY NOT HERE</b> and live in
    /// <see cref="VulkanAcquireRing"/> instead, indexed by a monotonic counter. An acquire semaphore is handed to
    /// <c>vkAcquireNextImageKHR</c> BEFORE the image index is known, so it cannot belong to an image, and putting
    /// it here is exactly the reuse bug V-F5 names.</para>
    ///
    /// <para><b>THE IMAGES ARE NOT OURS AND ARE NEVER DESTROYED.</b> They belong to the presentation engine, have
    /// no allocation of ours behind them and do not go through the memory allocator or the retire list. The views
    /// and the semaphores ARE ours and are destroyed here, which is legal because the caller drains the queue
    /// before disposing a generation.</para>
    /// </summary>
    internal sealed class VulkanSwapchainGeneration : IDisposable
    {
        readonly IVulkanSwapchainApi _api;
        readonly ulong[] _images;
        readonly ulong[] _views;
        readonly ulong[] _renderFinished;
        readonly VulkanAttachment[] _attachments;

        bool _disposed;

        VulkanSwapchainGeneration(IVulkanSwapchainApi api, ulong handle, in VulkanSwapchainSpec spec,
            GpuPixelFormat seamFormat, ulong[] images, ulong[] views, ulong[] renderFinished)
        {
            _api = api;
            Handle = handle;
            Spec = spec;
            SeamFormat = seamFormat;
            _images = images;
            _views = views;
            _renderFinished = renderFinished;

            _attachments = new VulkanAttachment[images.Length];
            for (int i = 0; i < images.Length; i++)
                _attachments[i] = new VulkanAttachment(views[i], images[i], seamFormat, DepthStencil: false);
        }

        /// <summary>The <c>VkSwapchainKHR</c> handle.</summary>
        internal ulong Handle { get; }

        /// <summary>Exactly what this generation was created with, so a later boundary can tell a present-mode
        /// change from a resize without re-deriving either.</summary>
        internal VulkanSwapchainSpec Spec { get; }

        /// <summary>The image format as the seam names it.</summary>
        internal GpuPixelFormat SeamFormat { get; }

        /// <summary>The extent every image in this generation was created at.</summary>
        internal VulkanExtent Extent => Spec.Extent;

        /// <summary>How many images the driver actually gave, which can exceed what the spec asked for.</summary>
        internal int ImageCount => _images.Length;

        /// <summary>The attachment for image <paramref name="index"/>, which is what the swapchain framebuffer
        /// publishes when that image is acquired.</summary>
        internal VulkanAttachment AttachmentAt(int index) => _attachments[index];

        /// <summary>The render-finished semaphore for image <paramref name="index"/>: signalled by the submit
        /// that renders into it, waited by the present of it.</summary>
        internal ulong RenderFinishedAt(int index) => _renderFinished[index];

        /// <summary>
        /// CREATE A GENERATION, or answer null when <c>vkCreateSwapchainKHR</c> refused.
        /// <para>
        /// A NULL RETURN IS NOT A THROW, deliberately. The present boundary's answer to a failed creation is to
        /// KEEP the generation it already has and try again at the next boundary, which is what stops a surface
        /// mid-resize from taking the frame loop down. A throw here would have to be caught two frames later by
        /// somebody who could do nothing more useful with it.
        /// </para>
        /// </summary>
        /// <param name="api">The swapchain seam.</param>
        /// <param name="surface">The surface to present to.</param>
        /// <param name="spec">The create-info, already decided and already known creatable.</param>
        /// <param name="oldSwapchain">The handle being replaced, or 0. Still the caller's to destroy.</param>
        /// <param name="failure">On a null return, the reason, for the caller to log.</param>
        internal static VulkanSwapchainGeneration? TryCreate(IVulkanSwapchainApi api, ulong surface,
            in VulkanSwapchainSpec spec, ulong oldSwapchain, out string? failure)
        {
            ArgumentNullException.ThrowIfNull(api);

            ulong handle = api.CreateSwapchain(surface, spec, oldSwapchain, out failure);
            if (handle == 0)
            {
                failure ??= "vkCreateSwapchainKHR returned no handle and no reason";
                return null;
            }

            // THE SEAM FORMAT IS RESOLVED BEFORE ANY VIEW IS MADE, so a surface format the seam cannot name fails
            // with one swapchain to destroy rather than with a chain of half-built views to unwind.
            GpuPixelFormat seamFormat;
            try
            {
                seamFormat = VulkanSwapchainPolicy.SeamFormatFor(spec.Format);
            }
            catch
            {
                api.DestroySwapchain(handle);
                throw;
            }

            IReadOnlyList<ulong> images = api.GetImages(handle);
            if (images.Count == 0)
            {
                api.DestroySwapchain(handle);
                failure = "vkGetSwapchainImagesKHR reported no images for a swapchain that was just created";
                return null;
            }

            var imageArray = new ulong[images.Count];
            var views = new ulong[images.Count];
            var renderFinished = new ulong[images.Count];

            for (int i = 0; i < images.Count; i++)
            {
                imageArray[i] = images[i];
                views[i] = api.CreateImageView(images[i], spec.Format);
                renderFinished[i] = api.CreateBinarySemaphore();
            }

            failure = null;
            return new VulkanSwapchainGeneration(api, handle, spec, seamFormat, imageArray, views, renderFinished);
        }

        /// <summary>
        /// Destroy the views, the semaphores and the swapchain, in that order. The images are the presentation
        /// engine's and are left alone.
        /// <para>
        /// THE CALLER HAS ALREADY DRAINED. A render-finished semaphore a submit signalled that no present waited
        /// on is left PENDING, and destroying a pending semaphore is undefined behaviour. There is no way to ask a
        /// binary semaphore whether it is pending, so the only safe retirement point is one where the queue is
        /// provably idle. That is why the recreate's drain is UNCONDITIONAL rather than resize-only, and it is the
        /// hazard that bites: drivers mostly tolerate the violation until they do not.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            for (int i = 0; i < _views.Length; i++) _api.DestroyImageView(_views[i]);
            for (int i = 0; i < _renderFinished.Length; i++) _api.DestroySemaphore(_renderFinished[i]);
            _api.DestroySwapchain(Handle);
        }
    }
}
