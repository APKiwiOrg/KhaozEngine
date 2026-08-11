using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// WHAT A NIL-DRAWABLE FRAME BINDS (M-W5): one device-owned colour texture at the current drawable size
    /// clamped to a minimum of one by one, matching the swapchain framebuffer's format.
    ///
    /// <para><b>THE ALTERNATIVE IS WHAT THE INCUMBENT DOES, AND IT IS THE REGRESSION THIS ROW EXISTS FOR.</b>
    /// <c>MTLSwapchainFramebuffer.IsRenderable</c> goes false when <c>CurrentDrawable</c> is nil,
    /// <c>PreDrawCommand</c> then returns false for every draw, and a whole frame's recording is built and thrown
    /// away with nothing logged and nothing counted. An orphan target costs ONE texture in a state a minimised
    /// window reaches and nothing else does, and it buys a frame that records, submits and completes exactly like
    /// any other with only its PRESENT skipped.</para>
    ///
    /// <para><b>IT IS A SEAM RATHER THAN A FIELD FOR TWO REASONS, and the lock one is the load-bearing half.</b>
    /// Creating a texture on this backend can append to the device's setup command buffer under the SETUP lock,
    /// and the setup lock is taken BEFORE the submit lock and never after it (M-W8). The present boundary holds
    /// the submit lock across its present and its apply, so it cannot create anything there, and behind this
    /// interface <see cref="Ensure"/> is called with NO lock held. The second reason is that it lets the whole
    /// boundary run under <c>dotnet test</c> against a fake that hands back a plain number.</para>
    ///
    /// <para><b>ITS LIFETIME IS THE DEVICE's, WHICH IS THE OTHER HALF OF M-W5's SAFETY RULE.</b> A recording that
    /// bound the orphan target must not be left naming a destroyed texture, so it is created lazily the first time
    /// this path is reached and destroyed at the NEXT SUCCESSFUL acquire rather than at the end of the frame that
    /// used it.</para>
    /// </summary>
    internal interface IMetalOrphanTarget
    {
        /// <summary>
        /// The orphan attachment at <paramref name="size"/>, creating it the FIRST time this path is reached and
        /// recreating it when the size has changed. Called on the submit thread with NO lock held.
        /// </summary>
        /// <param name="size">The size, already clamped to at least one by one by the caller.</param>
        /// <param name="format">The format the swapchain framebuffer publishes in its <c>Outputs</c>. Passed on
        /// every call rather than fixed at construction, so a pipeline built against the window stays valid while
        /// the orphan is bound: an orphan in another format would be validated against the wrong output
        /// description on the first draw.</param>
        MetalAttachment Ensure(MetalDrawableSize size, GpuPixelFormat format);

        /// <summary>
        /// Destroy the orphan target if one exists, at the next successful acquire. A no-op when there is none,
        /// which is the common case: most devices never reach the nil-drawable path at all.
        /// </summary>
        void Release();
    }

    /// <summary>
    /// THE REAL ORPHAN TARGET: an engine texture created through the device's own resource path, so it is an
    /// ordinary <c>Private</c> <c>MTLTexture</c> with a render-target usage bit and is released like any other.
    /// <para>
    /// THE CREATION CALL TRAVELS IN AS A DELEGATE rather than being reached, which is the same shape the Vulkan
    /// sibling uses and here it also keeps <see cref="MetalResourceFactory"/> out of the boundary's field graph.
    /// M-M10's architecture walk asserts no view factory is reachable from the recording type, and a resource
    /// path hanging off the present boundary is one edge away from being reachable from one.
    /// </para>
    /// </summary>
    internal sealed class MetalOrphanTarget : IMetalOrphanTarget
    {
        readonly Func<MetalDrawableSize, GpuPixelFormat, MetalTexture> _create;

        MetalTexture? _texture;
        MetalDrawableSize _size;
        GpuPixelFormat _format;

        /// <param name="create">Makes a render-target texture at a size and a format. The device's own factory
        /// call, handed in.</param>
        internal MetalOrphanTarget(Func<MetalDrawableSize, GpuPixelFormat, MetalTexture> create)
        {
            ArgumentNullException.ThrowIfNull(create);
            _create = create;
        }

        /// <summary>Whether a target exists right now, for the tests that pin the lazy creation and the release at
        /// the next successful acquire.</summary>
        internal bool IsLive => _texture is not null;

        /// <inheritdoc/>
        public MetalAttachment Ensure(MetalDrawableSize size, GpuPixelFormat format)
        {
            if (_texture is not null && _size == size && _format == format) return Attachment();

            // THE OLD ONE GOES FIRST, which is safe here and would not be at the other call site. Reaching this
            // branch means the size or the format moved between two nil-drawable boundaries, so the frame that
            // bound the previous target has already been submitted and the boundary is what is running now.
            Release();

            _texture = _create(size, format);
            _size = size;
            _format = format;
            return Attachment();
        }

        /// <inheritdoc/>
        public void Release()
        {
            _texture?.Dispose();
            _texture = null;
        }

        MetalAttachment Attachment() => new(_texture!.Handle.Handle, _format);
    }
}
