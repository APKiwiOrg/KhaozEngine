using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE FOUR NATIVE CALLS A SWAPCHAIN IS MADE OF, behind an interface for the same reason
    /// <see cref="ID3D11RingMemory"/> and <see cref="ID3D11FenceTimeline"/> are: everything that can be WRONG
    /// about a swapchain (when a resize is applied, which size wins when several arrive, whether the views are
    /// released before the buffers are resized, whether the framebuffer keeps its identity, what the sync
    /// interval is) is engine logic, and it is driven by plain <c>[Fact]</c>s on macOS and Linux with a fake
    /// behind this interface. What is left on the far side is <c>ResizeBuffers</c>, <c>GetBuffer</c> plus the view
    /// creations, the releases, and <c>Present</c>.
    /// <para>
    /// THE THREE-CALL RESIZE IS SPLIT DELIBERATELY, and the split is the point rather than an accident of
    /// factoring. <c>IDXGISwapChain::ResizeBuffers</c> FAILS while any outstanding reference to a backbuffer
    /// exists, so the render target view (and, when there is one, the depth texture sized to match) must be
    /// released first. Keeping <see cref="ReleaseAttachments"/>, <see cref="ResizeBuffers"/> and
    /// <see cref="CreateAttachments"/> as three members puts that ORDER in <see cref="D3D11Swapchain"/>, where a
    /// device-free test can assert it, instead of burying it inside one Windows-only body where only a Windows
    /// tester with a real window could ever find it broken. The incumbent has the same order and states it
    /// nowhere.
    /// </para>
    /// <para>
    /// THE FORMATS ARE READ OFF THE SURFACE rather than passed alongside it, so there is ONE source for what the
    /// backbuffer is. Decision W1 pins the colour format to <c>B8G8R8A8_UNorm</c> and the shipped windowed path
    /// asks for no depth attachment at all, but the swapchain still has to publish both through
    /// <see cref="IGpuFramebuffer.Outputs"/> for every pipeline that targets it, and a second copy of the answer
    /// is a second thing to get wrong.
    /// </para>
    /// <para>
    /// DISPOSAL RELEASES THE SWAPCHAIN ITSELF. The views go with <see cref="ReleaseAttachments"/>, which
    /// <see cref="IDisposable.Dispose"/> is expected to reach first, so a caller never has to sequence a teardown
    /// by hand.
    /// </para>
    /// </summary>
    internal interface ID3D11SwapchainSurface : IDisposable
    {
        /// <summary>The backbuffer's colour format. Decision W1 pins it to the incumbent's non-sRGB
        /// <c>B8G8R8A8_UNorm</c>, and it NEVER changes across a resize, which is why
        /// <see cref="IGpuFramebuffer.Outputs"/> can be computed once and every pipeline built against the
        /// swapchain survives a resize.</summary>
        GpuPixelFormat ColourFormat { get; }

        /// <summary>The depth attachment's format, or null when the swapchain carries none. The engine's own
        /// windowed creation path passes none, matching the incumbent, so this is null on every shipped
        /// path.</summary>
        GpuPixelFormat? DepthFormat { get; }

        /// <summary>
        /// Drop EVERY reference to the current backbuffer that <see cref="ResizeBuffers"/> can see: the views over
        /// it, the depth texture with them, and the immediate context's bind of those views. IDEMPOTENT, because
        /// the teardown path and the resize path both reach it and neither should have to ask what the other
        /// did.
        /// <para>
        /// This is the call <see cref="ResizeBuffers"/> cannot succeed without. It is not an optimisation and it
        /// is not tidiness: a live render target view over backbuffer 0 makes <c>ResizeBuffers</c> fail, and the
        /// window is then left presenting a swapchain whose buffers no longer match it.
        /// </para>
        /// <para>
        /// THE CONTEXT'S BIND IS A REFERENCE TOO, which is the half of the obligation an implementation will miss.
        /// <c>OMSetRenderTargets</c> takes its own reference on the render target view, and the context does not
        /// drop it because the application released its wrapper, so releasing only the views still fails the
        /// resize. This backend cannot inherit the incumbent's accidental immunity: the incumbent resets the
        /// context state at the end of every submit (<c>ExecuteCommandList</c> with <c>restoreContextState</c>
        /// false), while decision R3 puts this backend's one <c>ClearState</c> at the HEAD of a replay and the end
        /// of a submit emits nothing, so the last frame's targets are still bound when a resize applies at the
        /// present boundary.
        /// </para>
        /// <para>
        /// THE ENGINE HALF CANNOT ASSERT THAT DEVICE-FREE, and this is the one clause on this interface with no
        /// test above it. <see cref="D3D11Swapchain"/>'s tests drive a fake surface, which records that the release
        /// came before the resize and refuses the wrong order by name, but a fake has no context and therefore no
        /// bindings to inspect, so "the context's reference went too" is invisible to every <c>[Fact]</c>. The
        /// executable evidence is a real window resize on the WARP leg, once the wiring row gives the swapchain a
        /// caller.
        /// </para>
        /// </summary>
        void ReleaseAttachments();

        /// <summary>
        /// Resize the swapchain's buffers to <paramref name="width"/> by <paramref name="height"/>, keeping the
        /// buffer count, the format and the flags exactly as they were created (decision W1). The caller must
        /// have released the attachments first.
        /// <para>
        /// A zero width or height is passed through rather than rejected, because DXGI reads it as "match the
        /// window's client area" and that is the incumbent's behaviour on a minimised window. What comes back out
        /// of <see cref="CreateAttachments"/> is then the real size, which is why the attachments carry it.
        /// </para>
        /// </summary>
        void ResizeBuffers(uint width, uint height);

        /// <summary>
        /// Create the render target view over backbuffer 0 and, when <see cref="DepthFormat"/> is set, the depth
        /// texture and its view at the SAME size as the backbuffer. The returned attachments carry the
        /// backbuffer's real size, which is not necessarily <paramref name="width"/> by
        /// <paramref name="height"/>.
        /// <para>
        /// The parameters are the size the caller ASKED for, which the depth texture would otherwise have no
        /// other source for on a swapchain that has not been resized yet. An implementation that can read the
        /// real size (every real one can) is expected to prefer it for both, since Direct3D 11 requires a
        /// depth-stencil view and a render target view bound together to have matching dimensions.
        /// </para>
        /// </summary>
        D3D11SwapchainAttachments CreateAttachments(uint width, uint height);

        /// <summary>
        /// Present the backbuffer at <paramref name="syncInterval"/>, which decision W1 pins to 1 or 0 with no
        /// other throttling, and return the raw <c>HRESULT</c>.
        /// <para>
        /// THE RESULT IS RETURNED RATHER THAN CHECKED, and that is work-breakdown row 16's seam (decision G3):
        /// device loss is detected by checking the <c>HRESULT</c> after <c>Present</c>, calling
        /// <c>GetDeviceRemovedReason</c> IMMEDIATELY at the fault site and latching it. None of that is built
        /// here. What is built here is that the value reaches the caller at all, since the incumbent discards it
        /// and a discarded <c>HRESULT</c> is a device removal that surfaces several frames later as an unrelated
        /// crash.
        /// </para>
        /// </summary>
        int Present(int syncInterval);
    }
}
