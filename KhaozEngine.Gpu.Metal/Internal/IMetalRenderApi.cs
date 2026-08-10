using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE RENDER-ENCODER-SCOPED SETTERS, behind an interface so the schedule above them is device-free:
    /// <c>setViewport:</c> and <c>setScissorRect:</c>, which section 7.3 (M-A6, M-A7) owes three device-free
    /// assertions about.
    ///
    /// <para><b>THIS IS NOT <see cref="IMetalEncoderSink"/> AND MUST NOT BECOME IT.</b> That seam exists to be
    /// COUNTED: it covers the three call classes that scale with draw count, and M-T2 freezes a budget over it.
    /// Nothing here scales with draw count. A viewport and a scissor are emitted once per framebuffer CHANGE, so
    /// freezing a marginal over them would gate on a figure nobody should gate on, and widening the budget seam
    /// to reach them would quietly change what that budget means. So the rendering class gets its own line and
    /// the two seams stay separate on purpose rather than by omission.</para>
    ///
    /// <para><b>IT IS A LINE AT ALL BECAUSE THE DESIGN OWES A DEVICE-FREE TEST</b>, which is phase 3's row-12
    /// correction inherited rather than rediscovered. Section 7.3 asserts three things about the viewport and the
    /// scissor and every one of them is about an EMISSION: that both are emitted at a framebuffer change (a
    /// backend that does not emit rasterises nothing), that neither is emitted when the framebuffer did NOT
    /// change (an unconditional emit silently restores the full scissor and the next draw renders outside the
    /// intended rectangle, which is golden-visible and which phase 2's first spec froze the wrong way), and that
    /// the scissor is gated on the seam's own <c>ScissorTestEnabled</c> rather than on anything Metal has,
    /// because Metal has no scissor-test enable at all. The interop layer's calls are static P/Invoke, so an
    /// emission is observable only where there is a line to interpose on, and the alternative (assert the pure
    /// function and hope the call site passes it through) tests the arithmetic rather than the emission.</para>
    ///
    /// <para><b>WHERE THE RENDER ENCODER'S BEGIN AND END WENT.</b> Section 6.4 lists them here. They are on
    /// <see cref="IMetalEncoderSink"/> instead, and that seam's summary carries the reason: on this API the
    /// encoder boundary is a COUNTED class (M-T2's third one, which neither predecessor has), and a counted class
    /// has to be emitted through the seam the budget is frozen over. The half of 6.4's sentence whose reason does
    /// carry is the half about the setters, and it is what this interface is.</para>
    ///
    /// <para><b>HANDLES ARE <c>IntPtr</c> AND THE ARGUMENTS ARE THIS BACKEND'S OWN VALUES</b>, so a fake invents
    /// plain numbers and nothing above this line names an Objective-C type. <c>MTLViewport</c> is six doubles and
    /// <c>MTLScissorRect</c> four <c>NSUInteger</c>s in the interop layer, and neither shape is legible as a test
    /// expectation, which is the same split the Vulkan sibling's render API takes for the same reason.</para>
    /// </summary>
    internal interface IMetalRenderApi
    {
        /// <summary>
        /// <c>-[MTLRenderCommandEncoder setViewport:]</c>, the PLURAL-free form. M-A7 retires the incumbent's
        /// choice between <c>setViewports:count:</c> and the singular setter, which is a deprecated-feature-set
        /// read on the hot path to pick between two calls that do the same thing at count 1: the seam has no
        /// multi-viewport concept, so the count is always 1 and one code path is the answer.
        /// </summary>
        /// <param name="encoder">The open render encoder.</param>
        /// <param name="x">Origin x in pixels.</param>
        /// <param name="y">Origin y in pixels.</param>
        /// <param name="width">Width in pixels.</param>
        /// <param name="height">Height in pixels. POSITIVE, unlike the Vulkan sibling's, because Metal's clip
        /// space already matches the engine's and needs no negative-height trick (section 7.3).</param>
        /// <param name="minDepth">Near plane, 0 for every shipped pass.</param>
        /// <param name="maxDepth">Far plane, 1 for every shipped pass.</param>
        void SetViewport(IntPtr encoder, float x, float y, float width, float height,
            float minDepth, float maxDepth);

        /// <summary>
        /// <c>-[MTLRenderCommandEncoder setScissorRect:]</c>. Emitted only when the bound pipeline's
        /// <c>ScissorTestEnabled</c> says so, which is the backend honouring the SEAM's rasterizer state rather
        /// than the API's: Metal's rect is always live and defaults to the whole attachment, so not reproducing
        /// the gate would make a scissor set before a pipeline with the test off apply here and not on
        /// Direct3D 11.
        /// </summary>
        void SetScissorRect(IntPtr encoder, uint x, uint y, uint width, uint height);
    }
}
