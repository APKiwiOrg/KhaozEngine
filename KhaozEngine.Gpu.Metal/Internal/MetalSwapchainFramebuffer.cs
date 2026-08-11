using System;
using System.Threading;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE SWAPCHAIN'S <see cref="IGpuFramebuffer"/>, AND THE WHOLE OF M-W7: its OBJECT identity never changes,
    /// and everything underneath it does.
    ///
    /// <para><b>ON THIS API THE STABILITY IS FREE, WHICH IS THE DIFFERENCE FROM BOTH SIBLINGS.</b> W2 asked
    /// Direct3D 11 to behave like Metal here and V-W5 had to build it for Vulkan, because both of those keep a
    /// view object per backbuffer image that a resize destroys. Metal keeps none: a pass is an
    /// <c>MTLRenderPassDescriptor</c> built per pass from the attachment texture itself, so a resize has nothing
    /// to invalidate and this wrapper is the same object for the life of the device by construction rather than by
    /// discipline.</para>
    ///
    /// <para><b>THE <see cref="MetalBoundFramebuffer.Id"/> MOVES ON EVERY ACQUIRE, AND THAT IS THE ONE THING THAT
    /// IS NOT FREE.</b> M-A6's framebuffer-change guard is <c>if (framebuffer.Id == _framebuffer.Id) return;</c>,
    /// the first line of <see cref="MetalRenderPassSchedule.SetFramebuffer"/>, and it returns BEFORE copying the
    /// incoming record. So a source whose bound TEXTURE moves while its number stands still is a schedule that
    /// goes on describing the drawable the present has already moved past, with nothing anywhere reporting it. The
    /// Id therefore identifies the ATTACHMENT SET rather than the framebuffer OBJECT: an ordinary
    /// <see cref="MetalFramebuffer"/> never moves its handles and keeps one number forever, and this one takes a
    /// fresh number every time <see cref="Adopt"/> publishes a texture.</para>
    ///
    /// <para><b>AND MINTING A FRESH ONE COSTS NOTHING AT ALL, which is worth stating because the row 12 handoff
    /// weighed it as a cost.</b> An acquire happens only at the present boundary, so two
    /// <c>SetFramebuffer(swapchainFB)</c> calls inside ONE recording always see the same number and the redundant
    /// rebind is still correctly a no-op. The re-emitted viewport and scissor the handoff worried about would only
    /// arrive across a boundary, which starts a new recording that owes both anyway.</para>
    ///
    /// <para><b><see cref="Outputs"/> IS FIXED AT CONSTRUCTION</b>, exactly as it is on both siblings. A resize
    /// changes the size and never the format or the sample count, so every pipeline built against the window stays
    /// valid across every resize, and the orphan target is created in the SAME format for that reason.</para>
    ///
    /// <para><b>NO DEPTH ATTACHMENT AND NO MSAA, both matching the incumbent as the engine drives it.</b>
    /// <c>MTLSwapchainFramebuffer</c> creates a depth texture only when its <c>SwapchainDescription</c> carries a
    /// depth format, and the one windowed site in <c>GpuDeviceContext</c> passes null. The provider seam has no
    /// field for one either: <c>GpuWindowedDeviceRequest</c> carries a window, a size and a vsync flag. So the
    /// incumbent's inline depth-texture recreate (which releases a texture in-flight frames may still be reading,
    /// with no drain anywhere) has no occupant here, and M-W7's drain before the apply is what protects the
    /// drawable swap and would protect a depth rebuild the day the seam grows one.</para>
    ///
    /// <para><b>IT OWNS NOTHING AND ITS <see cref="Dispose"/> RELEASES NOTHING.</b> The colour attachment is the
    /// drawable's texture, owned by the drawable, or the device-owned orphan target. A consumer that disposes what
    /// <c>IGpuDevice.SwapchainFramebuffer</c> handed it therefore breaks nothing, which matches the incumbent's
    /// no-dispose wrapper over a device-owned swapchain framebuffer.</para>
    /// </summary>
    internal sealed class MetalSwapchainFramebuffer : IGpuFramebuffer, IMetalBoundFramebufferSource
    {
        // SHARED WITH MetalFramebuffer'S COUNTER would be tidier and is not available: that one is a private
        // static on a sealed type. A separate counter cannot collide with it either, because what the schedule
        // compares is the NUMBER rather than which counter produced it, so the two are kept apart and made
        // distinct by starting this one in the negative half of the space, which no framebuffer counter reaches.
        static long _nextId = long.MinValue;

        readonly MetalAttachment[] _colour = new MetalAttachment[1];

        MetalBoundFramebuffer _bound;

        /// <param name="colourFormat">The swapchain's colour format as the seam names it, fixed for the wrapper's
        /// life.</param>
        /// <param name="attachment">The first acquire's colour attachment.</param>
        /// <param name="size">The size that attachment was made at.</param>
        internal MetalSwapchainFramebuffer(GpuPixelFormat colourFormat, MetalAttachment attachment,
            MetalDrawableSize size)
        {
            // Sample count 1 and no depth, both pinned by the reproduction. See the type remarks.
            Outputs = new GpuOutputDescription(null, colourFormat).WithSampleCount(1);
            Adopt(attachment, size);
        }

        /// <inheritdoc/>
        public GpuOutputDescription Outputs { get; }

        /// <inheritdoc/>
        public uint Width { get; private set; }

        /// <inheritdoc/>
        public uint Height { get; private set; }

        /// <summary>
        /// How many attachments this wrapper has published, starting at 1. Present because stable OBJECT identity
        /// is exactly what makes an acquire invisible to anything holding this object: a diagnostic or a soak
        /// session that legitimately needs to know the drawable moved cannot learn it from the reference, and
        /// comparing sizes misses every acquire that did not resize.
        /// </summary>
        internal ulong Generation { get; private set; }

        /// <summary>The current colour attachment, for the tests that assert the publish ordering this type exists
        /// for.</summary>
        internal MetalAttachment Attachment => _colour[0];

        /// <inheritdoc/>
        MetalBoundFramebuffer IMetalBoundFramebufferSource.AsBound => _bound;

        /// <inheritdoc/>
        /// <remarks>True, and it is what the present path asks before deciding whether a frame has anything to
        /// present.</remarks>
        bool IMetalBoundFramebufferSource.IsSwapchain => true;

        /// <summary>
        /// PUBLISH A NEW ATTACHMENT UNDER A NEW <see cref="MetalBoundFramebuffer.Id"/> AND THE SAME OBJECT, which
        /// is the one mutation this type has. Called on the submit thread at the present boundary: once per
        /// successful acquire with the drawable's texture, and on the nil-drawable path with the device's orphan
        /// target.
        /// </summary>
        /// <param name="attachment">The colour attachment to bind from now on. Must be live at this instant,
        /// which is M-W5's ordering rule and is the caller's to honour.</param>
        /// <param name="size">Its size, which becomes the render area, the viewport and the full scissor.</param>
        internal void Adopt(MetalAttachment attachment, MetalDrawableSize size)
        {
            if (attachment.Texture == IntPtr.Zero)
            {
                throw new ArgumentException(
                    "The native Metal swapchain framebuffer was handed an attachment with no MTLTexture. A "
                    + "swapchain framebuffer always has a colour target: a nil drawable binds the device-owned "
                    + "orphan target (M-W5) rather than nothing, so a zero handle here means the orphan was "
                    + "published before it had been created, and binding it would rasterise into a pass Metal "
                    + "reports as having no attachments.",
                    nameof(attachment));
            }

            _colour[0] = attachment;
            Width = size.Width;
            Height = size.Height;
            Generation++;

            // The array is REUSED rather than replaced, so a bind allocates nothing. The record itself is rebuilt
            // because its Id, its width and its height are values, and the Id is the half M-A6's guard reads.
            _bound = new MetalBoundFramebuffer(
                unchecked((ulong)Interlocked.Increment(ref _nextId)), Width, Height, _colour, default,
                DepthHasStencil: false);
        }

        /// <summary>True once disposed. Nothing native is released and the wrapper keeps working: see the type
        /// remarks for why disposing the swapchain's framebuffer from outside is a no-op rather than an
        /// error.</summary>
        internal bool IsDisposed { get; private set; }

        /// <inheritdoc/>
        public void Dispose() => IsDisposed = true;
    }
}
