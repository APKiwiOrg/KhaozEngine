using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE SWAPCHAIN'S <see cref="IGpuFramebuffer"/>, AND THE WHOLE OF DECISION W2: its identity NEVER changes,
    /// and a resize swaps the views underneath it.
    /// <para>
    /// WHY THIS IS NOT <see cref="D3D11Framebuffer"/>. That type is an aggregate over engine textures whose views
    /// already exist, it creates nothing and it never changes after construction. This one wraps a backbuffer the
    /// runtime hands back and takes away again on every resize, so its attachments are mutable by nature. The two
    /// have opposite lifetimes and only the seam interface in common, which is why the resource row left this as
    /// a named open end rather than growing a mode into that type.
    /// </para>
    /// <para>
    /// WHAT STABLE IDENTITY REPLACES. The incumbent disposed the depth texture and the whole framebuffer on every
    /// resize and built a new object, which is why <c>VeldridGpuDevice.ResizeSwapchain</c> (deleted in 18.0.0)
    /// re-wrapped only on a reference change: a wrapper cached once would otherwise hand back a DISPOSED
    /// framebuffer, and the comment on that workaround named the Windows black screen after going fullscreen,
    /// maximising or drag-resizing. We own the wrapper, so we keep it. That makes Direct3D 11 behave the way
    /// Metal already does, which is the behaviour the rest of the engine was written against, and it deleted the
    /// workaround's reason to exist rather than guarding it.
    /// </para>
    /// <para>
    /// THE HAZARD STABLE IDENTITY CREATES, AND WHY IT DOES NOT BITE. Decision W6's guard in
    /// <see cref="D3D11DeviceState.BindFramebuffer"/> is REFERENCE identity, so re-binding this object after a
    /// resize reports no change and issues nothing, which would leave the context pointing at a released render
    /// target view and the viewport at the old size. Two facts make that unreachable, and both are structural.
    /// The resize is applied at the PRESENT boundary (decision W3), so it never lands in the middle of a
    /// recording or a replay. And decision R3 opens every replay with exactly one <c>ClearState</c>, whose
    /// <see cref="D3D11DeviceState.Reset"/> clears the bound framebuffer, so the first
    /// <c>SetFramebuffer</c> of the NEXT submit is always a change and always re-issues the render targets plus
    /// the full viewport at the new <see cref="Width"/> and <see cref="Height"/>. Remove either and this type
    /// becomes a silent black screen, which is why both are asserted together by test rather than left as two
    /// separate rules that happen to compose.
    /// </para>
    /// <para>
    /// <see cref="Outputs"/> IS FIXED AT CONSTRUCTION, deliberately. A resize changes the size and never the
    /// formats or the sample count, since the swapchain is recreated with neither (decision W1 pins
    /// <c>B8G8R8A8_UNorm</c> and <c>SampleDescription(1, 0)</c>), so every pipeline built against the swapchain
    /// stays valid across every resize. A framebuffer whose output description changed under a live pipeline
    /// would be a validation failure on the first draw after a resize.
    /// </para>
    /// <para>
    /// IT OWNS NOTHING AND ITS <see cref="Dispose"/> RELEASES NOTHING. The views belong to
    /// <see cref="ID3D11SwapchainSurface"/>, which has to release them in a specific order relative to
    /// <c>ResizeBuffers</c>, and the device owns the swapchain. A consumer that disposes what
    /// <c>IGpuDevice.SwapchainFramebuffer</c> handed it therefore breaks nothing, which matches the incumbent's
    /// no-dispose wrapper over the device-owned swapchain framebuffer.
    /// </para>
    /// </summary>
    internal sealed class D3D11SwapchainFramebuffer : IGpuFramebuffer, ID3D11RenderTargetSurface
    {
        object _renderTargetView;
        object? _depthStencilView;

        /// <summary>Build the wrapper over its first generation of attachments.</summary>
        /// <param name="colourFormat">The backbuffer's colour format, which never changes.</param>
        /// <param name="depthFormat">The depth attachment's format, or null when there is none.</param>
        /// <param name="attachments">The first generation of views, from
        /// <see cref="ID3D11SwapchainSurface.CreateAttachments"/>.</param>
        internal D3D11SwapchainFramebuffer(GpuPixelFormat colourFormat, GpuPixelFormat? depthFormat,
            in D3D11SwapchainAttachments attachments)
        {
            // Sample count 1, pinned by W1's SampleDescription(1, 0) and by the fact that a blit-model swapchain
            // cannot be multisampled without a resolve the present path does not have.
            Outputs = new GpuOutputDescription(depthFormat, colourFormat);
            _renderTargetView = attachments.RenderTargetView
                ?? throw new ArgumentException(NoRenderTarget, nameof(attachments));
            _depthStencilView = attachments.DepthStencilView;
            Width = attachments.Width;
            Height = attachments.Height;
            Generation = 1;
        }

        /// <inheritdoc/>
        public GpuOutputDescription Outputs { get; }

        /// <inheritdoc/>
        public uint Width { get; private set; }

        /// <inheritdoc/>
        public uint Height { get; private set; }

        /// <summary>The current render target view over backbuffer 0, as an <c>object</c> for the reason
        /// <see cref="D3D11SwapchainAttachments"/> gives. The real emitter casts it.</summary>
        internal object RenderTargetView => _renderTargetView;

        /// <summary>The current depth-stencil view, or null when the swapchain carries no depth attachment, which
        /// is every shipped path today.</summary>
        internal object? DepthStencilView => _depthStencilView;

        /// <summary>
        /// How many generations of attachments this wrapper has published, starting at 1 for the pair it was
        /// built with. Present because stable identity is exactly what makes a resize INVISIBLE to anything
        /// holding this object: a diagnostic, a soak session or a future consumer that legitimately needs to know
        /// the backbuffer was rebuilt cannot learn it from the reference, and comparing sizes misses a resize
        /// that came back to the same one.
        /// </summary>
        internal ulong Generation { get; private set; }

        /// <summary>
        /// PUBLISH A NEW GENERATION OF VIEWS UNDER THE SAME IDENTITY, which is the one mutation this type has.
        /// Called by <see cref="D3D11Swapchain"/> at the present boundary, under the submit lock, right after the
        /// surface created them.
        /// </summary>
        internal void Adopt(in D3D11SwapchainAttachments attachments)
        {
            _renderTargetView = attachments.RenderTargetView
                ?? throw new ArgumentException(NoRenderTarget, nameof(attachments));
            _depthStencilView = attachments.DepthStencilView;
            Width = attachments.Width;
            Height = attachments.Height;
            Generation++;
        }

        /// <summary>True once disposed. Nothing native is released, and the wrapper keeps working: see the type
        /// remarks for why disposing the swapchain's framebuffer from outside is a no-op rather than an
        /// error.</summary>
        internal bool IsDisposed { get; private set; }

        // ---- ID3D11RenderTargetSurface: the seam an emitter binds through, so one path covers this type and
        // D3D11Framebuffer. A blit swapchain has exactly one colour buffer, and the depth view is null on every
        // shipped path today. Both members read the CURRENT generation, which is the whole point of stable
        // identity: a resize swaps what these answer without changing who answers ----

        /// <inheritdoc/>
        int ID3D11RenderTargetSurface.RenderTargetCount => 1;

        /// <inheritdoc/>
        object ID3D11RenderTargetSurface.RenderTargetAt(int index) => index == 0
            ? _renderTargetView
            : throw new ArgumentOutOfRangeException(nameof(index), index,
                "A swapchain framebuffer has exactly one colour attachment.");

        /// <inheritdoc/>
        object? ID3D11RenderTargetSurface.DepthStencil => _depthStencilView;

        /// <inheritdoc/>
        public void Dispose() => IsDisposed = true;

        const string NoRenderTarget =
            "A swapchain framebuffer was handed attachments with no render target view. A swapchain always has a "
            + "colour target, so a null one means the surface returned before GetBuffer succeeded, and binding it "
            + "would rasterise into nothing with no error anywhere.";
    }
}
