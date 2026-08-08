using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// WHAT AN IMAGELESS FRAME BINDS (V-W4): one device-owned colour image at the current extent clamped to a
    /// minimum of 1 by 1, matching the swapchain framebuffer's shape and carrying no depth.
    ///
    /// <para><b>THE ALTERNATIVE WAS BINDING NOTHING, AND IT IS WORSE IN A WAY NO TEST WOULD FIND.</b> A frame
    /// whose swapchain could not be created has no image to render into. Leaving the framebuffer pointing at the
    /// views the recreate just destroyed is a use-after-free no CI leg in this fleet can see (MV9). Making
    /// <c>SetFramebuffer</c> illegal for that one frame means the recording path grows a second "no image yet"
    /// state and every consumer has to learn about it. An orphan target costs ONE image in a state a minimised or
    /// zero-extent window reaches and nothing else does, and it buys a frame that records, submits and completes
    /// exactly like any other, with only its PRESENT skipped.</para>
    ///
    /// <para><b>IT IS A SEAM RATHER THAN A FIELD BECAUSE OF THE LOCK ORDER.</b> Creating a texture on this
    /// backend appends its first-ever transition and its creation-time clear to the device's setup command buffer
    /// under the SETUP lock, and the setup lock is taken BEFORE the submit lock and never after it (V-W8). The
    /// present boundary holds the submit lock across its recreate, so it cannot create anything there. Behind this
    /// interface, <see cref="Ensure"/> is called with NO lock held, before the boundary takes the submit lock, on
    /// the one path that can need it. It also lets the whole boundary run under <c>dotnet test</c> against a fake
    /// that hands back a plain number.</para>
    /// </summary>
    internal interface IVulkanOrphanTarget
    {
        /// <summary>
        /// The orphan attachment at <paramref name="extent"/>, creating it the FIRST time this path is reached and
        /// recreating it when the extent has changed. Called on the submit thread with no lock held.
        /// </summary>
        /// <param name="extent">The size, already clamped to at least 1 by 1 by the caller.</param>
        /// <param name="format">The format the swapchain framebuffer publishes in its <c>Outputs</c>. Passed on
        /// every call rather than fixed at construction, because the framebuffer's format is decided from what the
        /// SURFACE offered and the orphan has to match it or every pipeline bound while it is up is validated
        /// against the wrong output description.</param>
        VulkanAttachment Ensure(VulkanExtent extent, GpuPixelFormat format);

        /// <summary>
        /// Destroy the orphan target if one exists, at the next successful acquire. A no-op when there is none,
        /// which is the common case: most devices never reach the imageless path at all.
        /// </summary>
        void Release();
    }

    /// <summary>
    /// THE TWO BINARY SEMAPHORES ONE FRAME'S SUBMIT CARRIES (V-W3), handed from the present boundary to the
    /// submit path and consumed exactly once.
    /// <para>
    /// EXACTLY ONCE IS THE CONTRACT AND IT IS WHY THIS IS TAKEN RATHER THAN READ. A binary semaphore may be
    /// waited once per signal, so a second submit in the same frame carrying the same wait semaphore is a wait
    /// nothing will ever satisfy, which is a hang rather than an error. The FIRST submit after an acquire takes
    /// the pair and every later one in that frame gets the default, which is no semaphores at all.
    /// </para>
    /// </summary>
    /// <param name="Wait">The acquire semaphore this submit waits on at <c>COLOR_ATTACHMENT_OUTPUT</c>, or 0 for
    /// none, which is the stall mode's shape and every submit after the first.</param>
    /// <param name="Signal">The acquired image's render-finished semaphore, which the present of that image waits
    /// on, or 0 for none.</param>
    internal readonly record struct VulkanFrameSemaphores(ulong Wait, ulong Signal)
    {
        /// <summary>Whether this pair carries anything at all.</summary>
        internal bool IsEmpty => Wait == 0 && Signal == 0;
    }

    /// <summary>
    /// THE REAL ORPHAN TARGET: an engine texture created through the device's own resource path, so it goes
    /// through the one allocator, gets its eager view and its resting layout, and is destroyed behind the
    /// timeline like every other resource.
    /// </summary>
    internal sealed class VulkanOrphanTarget : IVulkanOrphanTarget
    {
        readonly Func<VulkanExtent, GpuPixelFormat, IGpuTexture> _create;

        IGpuTexture? _texture;
        VulkanExtent _extent;
        GpuPixelFormat _format;

        /// <param name="create">Makes a render-target texture at a size and a format. The device's own factory
        /// call, handed in rather than reached, because a resource path reachable from here would put a view
        /// factory in a field graph that must not have one.</param>
        internal VulkanOrphanTarget(Func<VulkanExtent, GpuPixelFormat, IGpuTexture> create)
        {
            ArgumentNullException.ThrowIfNull(create);

            _create = create;
        }

        /// <summary>Whether a target currently exists. For the tests and for the device's teardown.</summary>
        internal bool IsLive => _texture is not null;

        /// <inheritdoc/>
        public VulkanAttachment Ensure(VulkanExtent extent, GpuPixelFormat format)
        {
            if (_texture is not null && _extent == extent && _format == format) return AttachmentOf(_texture);

            Release();
            _texture = _create(extent, format);
            _extent = extent;
            _format = format;
            return AttachmentOf(_texture);
        }

        /// <inheritdoc/>
        public void Release()
        {
            IGpuTexture? dying = _texture;
            _texture = null;
            _extent = default;
            dying?.Dispose();
        }

        static VulkanAttachment AttachmentOf(IGpuTexture texture)
        {
            var native = (VulkanTexture)texture;
            return new VulkanAttachment(native.AttachmentView, native.Image, native.Format, DepthStencil: false);
        }
    }
}
