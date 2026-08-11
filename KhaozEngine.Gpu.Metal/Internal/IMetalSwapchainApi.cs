using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// ONE ACQUIRED DRAWABLE: the retained <c>CAMetalDrawable</c> and the <c>MTLTexture</c> borrowed from it.
    /// <para>
    /// BOTH HANDLES TRAVEL TOGETHER because the texture's LIFETIME is the drawable's. Reading <c>-texture</c> at
    /// the acquire and carrying it is what makes the framebuffer's colour attachment a plain handle the recorder
    /// can bind with no Objective-C anywhere near it, which is what keeps
    /// <see cref="MetalRenderPassSchedule"/> constructible on a machine with no Metal.
    /// </para>
    /// </summary>
    /// <param name="Drawable">The retained drawable, or <see cref="IntPtr.Zero"/> when the layer had none to
    /// give, which is M-W5's whole condition.</param>
    /// <param name="Texture">Its <c>MTLTexture</c>, borrowed, or <see cref="IntPtr.Zero"/> with it.</param>
    internal readonly record struct MetalAcquiredDrawable(IntPtr Drawable, IntPtr Texture)
    {
        /// <summary>Whether an acquire produced anything. False is the nil-drawable frame.</summary>
        internal bool HasDrawable => Drawable != IntPtr.Zero;
    }

    /// <summary>
    /// EVERY NATIVE CALL THE PRESENT BOUNDARY MAKES, behind one narrow interface, so the boundary itself has no
    /// <c>CAMetalLayer</c>, no <c>MTLCommandQueue</c> and no <c>objc_msgSend</c> in it and runs under
    /// <c>dotnet test</c> on every leg.
    ///
    /// <para><b>THE SPLIT IS FORCED BY MM7 RATHER THAN CHOSEN FOR TIDINESS.</b> The design records that not one
    /// line of the incumbent's swapchain runs in CI on any leg, ever, and that zero automated coverage is what
    /// this row is answering. A headless runner cannot drive a real layer, so the only coverage available for the
    /// ORDER of a present boundary (present, then apply, then acquire, then publish), for the skipped present, for
    /// the counters and for the coalescing is coverage taken against a fake. This interface is the line that makes
    /// that possible, and <c>MetalSwapchainApi</c> is the seven-method implementation on the other side of
    /// it.</para>
    ///
    /// <para><b>IT IS DELIBERATELY NOT AN "IGpuSwapchain".</b> Every member here is one Objective-C message or a
    /// short fixed sequence of them, chosen so the real implementation has no decisions in it at all: which format
    /// the layer gets, what a zero size resolves to and when a present is skipped are all decided ABOVE this line,
    /// in <see cref="MetalSwapchainPolicy"/> and <see cref="MetalPresentBoundary"/>, where they are tested.</para>
    /// </summary>
    internal interface IMetalSwapchainApi : IDisposable
    {
        /// <summary>
        /// Write the layer's whole configuration, which is M-W1's field-for-field reproduction plus M-W4's one
        /// addition: <c>device</c>, <c>pixelFormat</c>, <c>framebufferOnly</c>, <c>drawableSize</c>,
        /// <c>displaySyncEnabled</c> and <c>maximumDrawableCount</c>. Called ONCE, at creation.
        /// </summary>
        /// <param name="size">The initial drawable size, already clamped to at least one by one.</param>
        /// <param name="colourSrgb">Whether the sRGB sibling of the colour format is wanted. Always
        /// <see cref="MetalSwapchainPolicy.ColourSrgbRequested"/> on the shipped path, and see that constant for
        /// why the other arm exists.</param>
        /// <param name="syncToVerticalBlank">The initial vsync value, written UNCONDITIONALLY (M-W2).</param>
        /// <param name="maximumDrawableCount">The drawable queue depth, which is
        /// <c>KE_METAL_FRAMES_IN_FLIGHT</c> (M-W4).</param>
        void Configure(MetalDrawableSize size, bool colourSrgb, bool syncToVerticalBlank,
            int maximumDrawableCount);

        /// <summary><c>-setDrawableSize:</c>. The whole of what a resize does on this API (M-W7).</summary>
        /// <param name="size">The new size, already clamped to at least one by one.</param>
        void SetDrawableSize(MetalDrawableSize size);

        /// <summary><c>-setDisplaySyncEnabled:</c>, written UNCONDITIONALLY (M-W2). The incumbent writes it only
        /// inside three values of an enum deprecated since macOS 10.15, so on a machine outside that set its
        /// vsync toggle silently does nothing.</summary>
        void SetDisplaySyncEnabled(bool enabled);

        /// <summary>
        /// <c>-nextDrawable</c>, RETAINED, or <see cref="MetalAcquiredDrawable.HasDrawable"/> false when the layer
        /// had none to give. IT BLOCKS and cannot be asked not to, which is why the caller times it (M-W4).
        /// </summary>
        MetalAcquiredDrawable NextDrawable();

        /// <summary>Release a drawable this api retained. Called exactly once per acquired drawable.</summary>
        void ReleaseDrawable(IntPtr drawable);

        /// <summary>
        /// M-W6's OWN COMMAND BUFFER for the present, at +1, or <see cref="IntPtr.Zero"/> when the queue would
        /// not make one.
        /// <para>
        /// IT IS A SEPARATE MEMBER SO THE CALLER CAN TAKE IT OUTSIDE THE SUBMIT LOCK, and that is the whole
        /// reason this is not one call with <see cref="PresentDrawable"/>. <c>-commandBuffer</c> BLOCKS once the
        /// queue's own maximum of uncommitted buffers is reached (see
        /// <see cref="MetalUncommittedBuffers"/>), and every commit that could release one goes through the
        /// submit lock, so blocking here while holding that lock is a deadlock rather than a stall. Called with
        /// NO lock held, for the same reason <see cref="NextDrawable"/> is.
        /// </para>
        /// </summary>
        IntPtr AcquirePresentBuffer();

        /// <summary>
        /// Present <paramref name="drawable"/> on <paramref name="commandBuffer"/> (M-W6): encode
        /// <c>-presentDrawable:</c>, commit, and release the buffer. One cheap object per frame, and 11.2 argues
        /// at length why the idiomatic alternative (encoding the present onto the frame's own buffer) is
        /// declined.
        /// <para>
        /// THE BUFFER SIGNALS NO TIMELINE VALUE, which is why teardown drains the QUEUE rather than the timeline.
        /// </para>
        /// </summary>
        /// <param name="commandBuffer">A buffer from <see cref="AcquirePresentBuffer"/>, never
        /// <see cref="IntPtr.Zero"/>. Released here, which is the one release of the acquire's retain.</param>
        /// <param name="drawable">The drawable to present.</param>
        void PresentDrawable(IntPtr commandBuffer, IntPtr drawable);
    }
}
