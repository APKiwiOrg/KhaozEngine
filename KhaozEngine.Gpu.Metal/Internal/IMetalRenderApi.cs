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
    ///
    /// <para><b>THE DESCRIPTOR PAIR IS A ROW-12 ADDITION, AND IT IS A CORRECTION TO ONE SENTENCE OF THE
    /// <see cref="IMetalEncoderSink"/> SUMMARY WITH ITS OWN REASON.</b> That summary lists clears among the
    /// things that go "straight to the interop layer with no indirection", and the reason it gives is about the
    /// BUDGET: a clear is a descriptor FIELD rather than a call, nothing about it scales with draw count, and
    /// freezing a marginal over it would gate on a figure nobody should gate on. All of that is right and none
    /// of it is a reason to put the call anywhere a test cannot see it. Section 18's row 12 requires the clear
    /// FOLDING, the load and store action selection and the deferred-begin state machine to run device-free, and
    /// the begin is what CONSUMES the pending clears, so a schedule that reached a static P/Invoke to build its
    /// descriptor could not open a pass at all on the Linux and Windows legs. So the descriptor crosses the
    /// UNCOUNTED seam, which keeps both properties: it is not in M-T2's budget, and it is observable. The pair
    /// is here rather than on the counted seam for exactly the reason the viewport and the scissor are.</para>
    ///
    /// <para><b>AND THE DESCRIPTOR IS RETAINED ON THE WAY OUT, WHICH IS WHY THERE ARE TWO MEMBERS AND NOT
    /// ONE.</b> <c>+renderPassDescriptor</c> is a convenience constructor, so the object it hands back dies with
    /// whatever autorelease pool was in scope when it was made, and the pass is built in one managed call and
    /// opened in another. One retain per <see cref="CreateRenderPassDescriptor"/> and exactly one
    /// <see cref="ReleaseRenderPassDescriptor"/> at every exit, including the exit where the encoder came back
    /// nil, which is the same ownership rule the encoder itself is under.</para>
    /// </summary>
    internal interface IMetalRenderApi
    {
        /// <summary>
        /// BUILD THE <c>MTLRenderPassDescriptor</c> FOR ONE PASS, and take a retain on it (see the type remarks).
        /// <para>
        /// EVERY DECISION IS ALREADY MADE BY THE TIME IT ARRIVES HERE. Which attachment clears and to what
        /// (M-A2), what each load action is, and that every store action is <c>Store</c> rather than the
        /// descriptor's discarding default (M-A4), are all fields of the plan
        /// <see cref="MetalRenderPassSchedule"/> computed. This member translates and makes native calls, so a
        /// fake can record the plan verbatim and every rule in section 7.1 and 7.2 is asserted with no Metal
        /// anywhere.
        /// </para>
        /// </summary>
        /// <param name="colour">The colour attachments in order, possibly empty for a depth-only shadow
        /// pass.</param>
        /// <param name="depth">The depth attachment, or one whose texture is <see cref="IntPtr.Zero"/> when the
        /// framebuffer declares none.</param>
        /// <returns>The retained descriptor, or <see cref="IntPtr.Zero"/> when Metal would not make one.</returns>
        IntPtr CreateRenderPassDescriptor(ReadOnlySpan<MetalColourAttachment> colour,
            in MetalDepthAttachment depth);

        /// <summary>Give back the retain <see cref="CreateRenderPassDescriptor"/> took. Safe on
        /// <see cref="IntPtr.Zero"/>, so the caller's <c>finally</c> needs no test of its own.</summary>
        void ReleaseRenderPassDescriptor(IntPtr descriptor);

        /// <summary>
        /// <c>-[MTLRenderCommandEncoder setViewports:count:]</c> WITH A COUNT OF 1, which is M-A7 taken
        /// literally. It retires the incumbent's choice between that selector and the singular
        /// <c>setViewport:</c> on <c>IsSupported(macOS_GPUFamily1_v3)</c>, a deprecated-feature-set read on the
        /// hot path to pick between two calls that do the same thing at count 1: the seam has no multi-viewport
        /// concept, so the count is always 1 and one code path is the answer.
        /// <para>
        /// ROW 7 DECLARED THIS MEMBER AS THE SINGULAR SELECTOR AND ROW 12 CORRECTED IT IN PLACE, because M-A7
        /// names the plural forms in as many words and both rows agree on the part that matters (no conditional
        /// and one code path). The plural also happens to REMOVE an ABI question: the singular setters pass their
        /// structs by value, which is arm64's indirect-composite path, and the plural ones pass an array address
        /// and a count, which is the plain register class row 1's spike used throughout. The seam's own signature
        /// is unchanged and stays scalar, because a count of 1 is not something a caller should be able to say
        /// otherwise.
        /// </para>
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
        /// <c>-[MTLRenderCommandEncoder setScissorRects:count:]</c> with a count of 1, the scissor half of M-A7
        /// and corrected in place for the same reason. Emitted only when the bound pipeline's
        /// <c>ScissorTestEnabled</c> says so, which is the backend honouring the SEAM's rasterizer state rather
        /// than the API's: Metal's rect is always live and defaults to the whole attachment, so not reproducing
        /// the gate would make a scissor set before a pipeline with the test off apply here and not on
        /// Direct3D 11.
        /// </summary>
        void SetScissorRect(IntPtr encoder, uint x, uint y, uint width, uint height);
    }
}
