using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// DECISION M-T2's INTERPOSITION POINT: the narrow seam the device-free native-call BUDGET test counts
    /// through, covering ONLY the three call classes that scale with draw count ON THIS API. Argument-table
    /// writes, draws and dispatches, and ENCODER BOUNDARIES. Nothing else.
    ///
    /// <para><b>WHY A SEAM AT ALL.</b> The interop layer's calls are static <c>[LibraryImport]</c> P/Invokes, so
    /// there is no way to observe what a recorder asked the driver for without a line to interpose on. The
    /// Direct3D 11 backend answered the same question with <c>ID3D11BindSink</c> and the Vulkan backend with
    /// <c>IVkCmdSink</c>, and this is that shape aimed at a third animal.</para>
    ///
    /// <para><b>AIMING THIS AT EITHER NEIGHBOUR'S CALL CLASSES WOULD HAVE BEEN THE MISTAKE, TWICE OVER.</b>
    /// Direct3D 11's fan-out class is one native call per resource per stage, because that API binds RESOURCES.
    /// Vulkan's is per-draw descriptor set ALLOCATION and per-draw <c>vkUpdateDescriptorSets</c>, and Metal
    /// allocates no descriptor of any kind. Metal's is argument-table writes AND an ENCODER BOUNDARY per
    /// record-time upload, and the second has no analogue anywhere else in the program. A budget ported from
    /// either predecessor would pass green while a record-time <c>UpdateBuffer</c> split the encoder a thousand
    /// times a frame.</para>
    ///
    /// <para><b>THE ENCODER BOUNDARY EMITS THROUGH THIS SEAM, WHICH IS A ROW-7 CORRECTION TO SECTION 6.4's
    /// PLACEMENT AND THE REASON IS M-T2 ITSELF.</b> 6.4 puts the render-encoder begin and end pair on
    /// <see cref="IMetalRenderApi"/> alongside the viewport and scissor setters, by analogy with phase 3, where
    /// <c>vkCmdBeginRendering</c> is deliberately NOT on the counted seam because nothing about it scales with
    /// draw count. That analogy does not carry: on Metal the boundary IS a counted class, named in M-T2 and
    /// bolded in the work-breakdown row, precisely because it is the thing a record-time upload multiplies. A
    /// class that is counted has to be emitted through the seam the budget is frozen over, or the budget counts
    /// what a recorder REPORTS rather than what it EMITS, which is the exact weakness 6.4's own sentence about
    /// interposition warns against. So the boundary members live here and
    /// <see cref="IMetalRenderApi"/> keeps the render-encoder-scoped setters, which is the half of 6.4 whose
    /// reason does carry.</para>
    ///
    /// <para><b>CONSUMED THROUGH A GENERIC CONSTRAINT (<c>where TSink : struct, IMetalEncoderSink</c>) ON THE
    /// PER-DRAW PATH, AND AS AN INTERFACE FIELD ON THE BOUNDARY PATH.</b> The JIT monomorphizes each
    /// implementation, so a recorder written against the argument-table and draw members carries no interface
    /// dispatch and boxes nothing. The boundary members are reached by
    /// <see cref="MetalEncoderScope"/> through a plain field, because an encoder transition happens a handful of
    /// times per frame and the seam members that cause one (<c>IGpuCommandList.SetFramebuffer</c> among them)
    /// are interface members that cannot be generic. One virtual call per PASS is not the cost the
    /// struct constraint exists to avoid, and making the command list generic over its sink to remove it would
    /// put the sink type in the signature of every type that holds a list.</para>
    ///
    /// <para><b>EVERY IMPLEMENTATION IS A READONLY STRUCT WHOSE MUTABLE STATE SITS BEHIND A CLASS
    /// REFERENCE</b>, which is the emitter rule both sibling backends enforce and which is load-bearing here for
    /// a reason they do not have: this seam is used boxed AND unboxed in the same recording, so a sink with a
    /// mutable field would count boundaries into one copy and draws into another.</para>
    ///
    /// <para><b>WHAT DELIBERATELY GOES STRAIGHT TO THE INTEROP LAYER WITH NO INDIRECTION:</b> clears (which are
    /// descriptor FIELDS rather than calls), copies, mip generation, resolves, and the pipeline-state block.
    /// Nothing about any of them scales per draw, and freezing numbers over them would gate on figures nobody
    /// should gate on.</para>
    ///
    /// <para><b>HANDLES ARE <c>IntPtr</c> AND NOTHING HERE NAMES AN OBJECTIVE-C TYPE.</b> A fake invents plain
    /// numbers, so the budget test and the recording-contract tests stay device-free and run on the Linux and
    /// Windows legs where no Metal exists at all.</para>
    /// </summary>
    internal interface IMetalEncoderSink
    {
        // ---- Encoder boundaries, the Metal-specific class ---------------------------------------------------

        /// <summary>
        /// <c>-[MTLCommandBuffer renderCommandEncoderWithDescriptor:]</c>, the deferred begin's one native call
        /// (M-A1). The descriptor carries the attachments, the folded clears and the explicit store actions, and
        /// building it is row 12's (https://github.com/APKiwiOrg/KhaozEngine/issues/578).
        /// </summary>
        /// <param name="commandBuffer">The buffer being recorded into.</param>
        /// <param name="descriptor">The <c>MTLRenderPassDescriptor</c>.</param>
        /// <returns>The encoder, or <see cref="IntPtr.Zero"/> when Metal would not make one, which M-W5's
        /// orphan-target rule is the answer to.</returns>
        IntPtr BeginRenderEncoder(IntPtr commandBuffer, IntPtr descriptor);

        /// <summary><c>-[MTLCommandBuffer blitCommandEncoder]</c>. The kind 2.1 is about: opening one ends a
        /// render encoder, so a record-time upload that takes this path costs a whole re-activation on the next
        /// draw.</summary>
        IntPtr BeginBlitEncoder(IntPtr commandBuffer);

        /// <summary><c>-[MTLCommandBuffer computeCommandEncoderWithDispatchType:]</c> with the SERIAL dispatch
        /// type, which is what makes M-H4 true: dispatches inside one encoder do not overlap, so the backend
        /// needs no dependent-dispatch hazard machinery.</summary>
        IntPtr BeginComputeEncoder(IntPtr commandBuffer);

        /// <summary>
        /// <c>-[MTLCommandEncoder endEncoding]</c>. The selector is the same for all three kinds and
        /// <paramref name="kind"/> is carried so a counting sink can attribute the boundary without holding
        /// state of its own.
        /// </summary>
        void EndEncoding(MetalEncoderKind kind, IntPtr encoder);

        // ---- Argument-table writes ---------------------------------------------------------------------------

        /// <summary>
        /// The ARRAY buffer setter for <paramref name="stage"/>
        /// (<c>setVertexBuffers:offsets:withRange:</c> and its siblings), over a CONTIGUOUS RUN of argument-table
        /// indices starting at <paramref name="firstIndex"/>.
        /// <para>
        /// THERE IS NO SINGLE-ELEMENT OVERLOAD, deliberately, and it is the same rule <c>ID3D11BindSink</c>
        /// expresses by having only array calls. M-R6's law is one call per (kind, stage) per flush, so a
        /// per-element entry point would be the #418 fan-out defect available as an API. The incumbent emits one
        /// call per element per stage and the vendored fork's binding does not declare a single array setter,
        /// which is what this member exists to beat.
        /// </para>
        /// </summary>
        void SetBuffers(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> buffers,
            ReadOnlySpan<nuint> offsets, uint firstIndex);

        /// <summary>The array texture setter for <paramref name="stage"/>
        /// (<c>setFragmentTextures:withRange:</c> and its siblings). Same law, same reason.</summary>
        void SetTextures(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> textures, uint firstIndex);

        /// <summary>The array sampler setter for <paramref name="stage"/>
        /// (<c>setFragmentSamplerStates:withRange:</c> and its siblings).</summary>
        void SetSamplerStates(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> samplers,
            uint firstIndex);

        /// <summary>
        /// THE OFFSETS-ONLY REBIND (M-R7): <c>setVertexBufferOffset:atIndex:</c> or its stage sibling, ONE call
        /// per VISIBLE stage, with no buffer rebind and no argument-table write behind it. An integer into the
        /// encoder's stream.
        /// <para>
        /// It is a DIFFERENT CALL rather than a cheaper variant of the array setter, which is why M-R5's dirty
        /// record has two states here and three on Direct3D 11: there the third state exists to skip textures and
        /// samplers, and here the offsets-only path does not go through the same selector at all. This is the
        /// shadow pass's shape thousands of times a frame.
        /// </para>
        /// </summary>
        void SetBufferOffset(MetalShaderStage stage, IntPtr encoder, nuint offset, uint index);

        // ---- Draws and dispatches ----------------------------------------------------------------------------

        /// <summary><c>-[MTLRenderCommandEncoder drawPrimitives:vertexStart:vertexCount:instanceCount:baseInstance:]</c>.
        /// Row 14 owns the emission (https://github.com/APKiwiOrg/KhaozEngine/issues/580).</summary>
        void Draw(IntPtr encoder, uint vertexStart, uint vertexCount, uint instanceCount, uint baseInstance);

        /// <summary><c>-[MTLRenderCommandEncoder drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:instanceCount:baseVertex:baseInstance:]</c>.
        /// The index buffer travels in the call rather than being bound beforehand, which is Metal's shape and
        /// not an engine choice. Row 14 owns the emission.</summary>
        void DrawIndexed(IntPtr encoder, uint indexCount, IntPtr indexBuffer, nuint indexBufferOffset,
            bool sixteenBitIndices, uint instanceCount, int baseVertex, uint baseInstance);

        /// <summary><c>-[MTLComputeCommandEncoder dispatchThreadgroups:threadsPerThreadgroup:]</c>. Row 14 owns
        /// the emission.</summary>
        void Dispatch(IntPtr encoder, uint groupCountX, uint groupCountY, uint groupCountZ,
            uint threadsPerGroupX, uint threadsPerGroupY, uint threadsPerGroupZ);
    }
}
