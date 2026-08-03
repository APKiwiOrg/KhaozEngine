namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// ONE GENERATION OF THE SWAPCHAIN'S BACKBUFFER VIEWS, handed from
    /// <see cref="ID3D11SwapchainSurface.CreateAttachments"/> to <see cref="D3D11SwapchainFramebuffer"/> so the
    /// framebuffer can swap what it points at while staying the same object (decision W2).
    /// <para>
    /// THE VIEWS ARE TYPED <c>object</c> HERE, for the reason <see cref="ID3D11PipelineState"/> states at length:
    /// a Vortice type in this signature would put the interop on the load path of every device-free test, and the
    /// only thing the engine half does with a view is carry it and compare it. The real emitter casts them back to
    /// <c>ID3D11RenderTargetView</c> and <c>ID3D11DepthStencilView</c> inside its own guarded body, exactly as it
    /// already casts an <see cref="IGpuPipeline"/> to the concrete pipeline type to read its typed fields.
    /// </para>
    /// <para>
    /// THE SIZE COMES BACK FROM THE SURFACE RATHER THAN BEING ECHOED FROM THE REQUEST, and that is a fidelity
    /// point rather than a convenience. The incumbent builds its swapchain framebuffer out of a wrapper over the
    /// texture <c>GetBuffer(0)</c> returned, and that wrapper reads its width and height off the real resource
    /// description, so the framebuffer reports what the backbuffer ACTUALLY is. That matters because DXGI reads a
    /// zero width or height in <c>ResizeBuffers</c> as "match the window's client area", which the windowing layer
    /// does send: the Silk framebuffer-resize callback forwards a minimised window as 0 by 0. Trusting the
    /// requested size would leave the framebuffer claiming 0 by 0 while the backbuffer is whatever the window is,
    /// and the viewport that <c>SetFramebuffer</c> derives from it (decision W6) would rasterise nothing.
    /// </para>
    /// <para>
    /// NOTHING HERE IS OWNED. The surface creates these objects and releases them in
    /// <see cref="ID3D11SwapchainSurface.ReleaseAttachments"/>, because the release has to happen before
    /// <see cref="ID3D11SwapchainSurface.ResizeBuffers"/> and the surface is the only party that can order those
    /// two. A framebuffer that owned its views would put that ordering on the wrong object.
    /// </para>
    /// </summary>
    internal readonly struct D3D11SwapchainAttachments
    {
        /// <summary>Build one generation of attachments over the backbuffer's ACTUAL size.</summary>
        /// <param name="width">The backbuffer's real width, read off the resource.</param>
        /// <param name="height">The backbuffer's real height, read off the resource.</param>
        /// <param name="renderTargetView">The render target view over backbuffer 0.</param>
        /// <param name="depthStencilView">The depth-stencil view, or null when the swapchain carries no depth
        /// attachment, which is what the shipped windowed path asks for.</param>
        internal D3D11SwapchainAttachments(uint width, uint height, object renderTargetView,
            object? depthStencilView)
        {
            Width = width;
            Height = height;
            RenderTargetView = renderTargetView;
            DepthStencilView = depthStencilView;
        }

        /// <summary>The backbuffer's real width.</summary>
        internal uint Width { get; }

        /// <summary>The backbuffer's real height.</summary>
        internal uint Height { get; }

        /// <summary>The render target view over backbuffer 0. Never null: a swapchain with no colour target is
        /// not a thing this backend can build.</summary>
        internal object RenderTargetView { get; }

        /// <summary>The depth-stencil view, or null when there is no depth attachment.</summary>
        internal object? DepthStencilView { get; }
    }
}
