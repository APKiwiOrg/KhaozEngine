using System;
using System.Threading;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE SWAPCHAIN'S <see cref="IGpuFramebuffer"/>, AND THE WHOLE OF DECISION V-W5: its identity NEVER changes,
    /// and everything underneath it does.
    ///
    /// <para><b>IDENTITY STABILITY MATTERS MORE HERE THAN ON THE OTHER NATIVE BACKEND, because EVERY IMAGE VIEW
    /// OBJECT IS REPLACED.</b> A Direct3D 11 resize swaps one render-target view. A Vulkan recreate destroys the
    /// whole chain and makes a fresh view per image, and on top of that the bound attachment moves to a DIFFERENT
    /// image on every acquire even when nothing was recreated. So this wrapper changes what it points at far more
    /// often than the other one does, and the thing a consumer caches has to survive all of it.</para>
    ///
    /// <para><b>THE ONE RULE THAT MAKES A USE-AFTER-FREE UNREACHABLE, and it is an ORDERING rather than a
    /// guard.</b> This wrapper is repointed at a live view BEFORE the views it was pointing at are destroyed,
    /// every time, with both steps on the submit thread and no recording in flight. That is why
    /// <see cref="VulkanPresentBoundary"/> creates the new swapchain, publishes its views here and only then
    /// destroys the old chain, and why the one path where no new chain can be made (a window with a zero extent)
    /// publishes the device's ORPHAN TARGET here first instead. Recording against views a recreate destroyed is a
    /// use-after-free no CI leg in this fleet can see (MV9), so it is designed out rather than tested for.</para>
    ///
    /// <para><b><see cref="Outputs"/> IS FIXED AT CONSTRUCTION</b>, deliberately, exactly as it is on the other
    /// native backend. A recreate changes the size and never the format or the sample count, so every pipeline
    /// built against the swapchain stays valid across every recreate. A framebuffer whose output description
    /// changed under a live pipeline would be a validation failure on the first draw after a resize.</para>
    ///
    /// <para><b>NO DEPTH ATTACHMENT AND NO MSAA</b>, both matching the incumbent (V-W1). The engine's 3D path
    /// renders into its own targets and resolves into this one.</para>
    ///
    /// <para><b>IT OWNS NOTHING AND ITS <see cref="Dispose"/> RELEASES NOTHING.</b> The views belong to the
    /// swapchain generation that made them and the images belong to the presentation engine, so a consumer that
    /// disposes what <c>IGpuDevice.SwapchainFramebuffer</c> handed it breaks nothing. That matches the incumbent's
    /// no-dispose wrapper over the device-owned swapchain framebuffer.</para>
    /// </summary>
    internal sealed class VulkanSwapchainFramebuffer : IGpuFramebuffer, IVulkanBoundFramebufferSource
    {
        // SHARED WITH VulkanFramebuffer'S COUNTER would be tidier and is not available: that one is a private
        // static on a sealed type. A separate counter cannot collide with it either, because this type takes its
        // identity from the same Interlocked source the recorder compares, which is the number itself rather than
        // which counter produced it. So the two are kept apart and the ids are made distinct by starting this one
        // in the negative half of the space, which no framebuffer counter ever reaches.
        static long _nextId = long.MinValue;

        readonly VulkanAttachment[] _colour = new VulkanAttachment[1];

        VulkanBoundFramebuffer _bound;

        /// <param name="colourFormat">The swapchain's colour format as the seam names it, fixed for the
        /// wrapper's life.</param>
        /// <param name="attachment">The first generation's colour attachment.</param>
        /// <param name="extent">The size that attachment was made at.</param>
        internal VulkanSwapchainFramebuffer(GpuPixelFormat colourFormat, VulkanAttachment attachment,
            VulkanExtent extent)
        {
            // Sample count 1 and no depth, both pinned by V-W1's reproduction.
            Outputs = new GpuOutputDescription(null, colourFormat).WithSampleCount(1);
            Id = unchecked((ulong)Interlocked.Increment(ref _nextId));
            Adopt(attachment, extent);
        }

        /// <inheritdoc/>
        public GpuOutputDescription Outputs { get; }

        /// <inheritdoc/>
        public uint Width { get; private set; }

        /// <inheritdoc/>
        public uint Height { get; private set; }

        /// <summary>This framebuffer's process-unique identity, which the framebuffer-change guard compares. It
        /// is taken ONCE, at construction, and is the thing decision V-W5 is about.</summary>
        internal ulong Id { get; }

        /// <summary>
        /// How many generations of attachments this wrapper has published, starting at 1. Present because stable
        /// identity is exactly what makes a recreate INVISIBLE to anything holding this object: a diagnostic or a
        /// soak session that legitimately needs to know the chain was rebuilt cannot learn it from the reference,
        /// and comparing sizes misses a resize that came back to the same one. It also moves on every acquire,
        /// because an acquire republishes the attachment.
        /// </summary>
        internal ulong Generation { get; private set; }

        /// <summary>The current attachment, for the tests that assert the ordering rule this type exists
        /// for.</summary>
        internal VulkanAttachment Attachment => _colour[0];

        /// <inheritdoc/>
        VulkanBoundFramebuffer IVulkanBoundFramebufferSource.AsBound => _bound;

        /// <inheritdoc/>
        bool IVulkanBoundFramebufferSource.IsSwapchain => true;

        /// <summary>
        /// PUBLISH A NEW ATTACHMENT UNDER THE SAME IDENTITY, which is the one mutation this type has. Called on
        /// the submit thread at the present boundary: once per successful acquire with the acquired image's view,
        /// once per recreate with the new chain's first view, and on the imageless path with the device's orphan
        /// target.
        /// </summary>
        /// <param name="attachment">The colour attachment to bind from now on. Must be live at this instant, which
        /// is the ordering rule in the type remarks.</param>
        /// <param name="extent">Its size, which becomes the render area, the viewport and the full scissor.</param>
        internal void Adopt(VulkanAttachment attachment, VulkanExtent extent)
        {
            if (attachment.View == 0)
            {
                throw new ArgumentException(
                    "The native Vulkan swapchain framebuffer was handed an attachment with no VkImageView. A "
                    + "swapchain framebuffer always has a colour target, so a zero view means a recreate published "
                    + "before it had made one, and binding it would rasterise into nothing with no error anywhere.",
                    nameof(attachment));
            }

            _colour[0] = attachment;
            Width = extent.Width;
            Height = extent.Height;
            Generation++;

            // The array is REUSED rather than replaced, so a bind allocates nothing and every previously handed
            // out record sees the current attachment. The record itself is rebuilt because its width and height
            // are values.
            _bound = new VulkanBoundFramebuffer(Id, Width, Height, _colour, default);
        }

        /// <summary>True once disposed. Nothing native is released and the wrapper keeps working: see the type
        /// remarks for why disposing the swapchain's framebuffer from outside is a no-op rather than an
        /// error.</summary>
        internal bool IsDisposed { get; private set; }

        /// <inheritdoc/>
        public void Dispose() => IsDisposed = true;
    }
}
