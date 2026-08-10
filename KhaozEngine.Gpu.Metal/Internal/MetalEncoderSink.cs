using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE REAL <see cref="IMetalEncoderSink"/>: the emitting half of M-T2's budget seam, over the interop layer.
    ///
    /// <para><b>EACH CALL CLASS EMITS WHEN THE ROW THAT CALLS IT LANDS, AND TWO OF THE THREE ARE LIVE.</b> The
    /// SEAM covers all three classes and was complete from row 7, because a budget seam retrofitted after the
    /// recorder exists is a seam shaped by the recorder rather than by what needs counting, and phase 2 records
    /// exactly that outcome. The EMISSIONS are a different question: a native prototype added by a row that has
    /// no caller for it and no test that runs it is an Objective-C declaration nobody has ever executed, and row
    /// 1's own regression evidence is that a wrong ABI assumption in interop is a memory corruption rather than a
    /// compile error. The encoder boundary emitted from row 7
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/573), the four argument-table setters emit now that row
    /// 13's flush drives them (https://github.com/APKiwiOrg/KhaozEngine/issues/579), and the draws refuse until
    /// row 14 issues them (https://github.com/APKiwiOrg/KhaozEngine/issues/580).</para>
    ///
    /// <para><b>A READONLY STRUCT WITH NO STATE AT ALL</b>, which is the emitter rule
    /// <see cref="IMetalEncoderSink"/> states. It matters more here than in either sibling because this seam is
    /// consumed BOXED on the boundary path and UNBOXED through a struct constraint on the per-draw path, so a
    /// sink with a mutable field would be two different objects in one recording.</para>
    ///
    /// <para><b>EVERY ENCODER IS RETAINED AT ITS BEGIN AND RELEASED AT ITS END, and that is the alternative to a
    /// pool that spans a recording.</b> The three factories hand back AUTORELEASED encoders, which have to
    /// outlive the pool that was in scope when they were made: a render encoder lives for a whole pass and the
    /// commands recorded into it come from the consumer's own call sites, so there is no scope this backend
    /// controls that covers it. The two answers are a pool opened at <c>Begin</c> and popped at the submit, which
    /// would also hold every other autoreleased object a frame's recording produces (M-N5 exists because that
    /// shape accumulates), or an explicit retain and release pair per boundary. The pair costs two C calls a
    /// handful of times per frame and makes the lifetime independent of any pool discipline at all, which is why
    /// it is what this takes.</para>
    ///
    /// <para><b>SO THE OWNERSHIP RULE IS THE COMMAND BUFFER'S, WORD FOR WORD, AND IT APPLIES TO ENCODERS TOO:
    /// EXACTLY ONE RELEASE PER ACQUISITION, ACROSS EVERY EXIT.</b> The exits are the ordinary end (a kind switch,
    /// or the <c>End</c> that seals a record), a recording abandoned by the next <c>Begin</c>, and a command list
    /// disposed mid-record, and <see cref="MetalEncoderScope.EnsureNoEncoder"/> is the one place all three reach.
    /// The reason to spell it here rather than trust the happy path is what an abandoned encoder actually costs.
    /// It is not one leaked encoder: an encoder holds a reference to its own command buffer, so the buffer stays
    /// alive after the list has released it and stays counted against the queue's maximum number of UNCOMMITTED
    /// buffers, which is 64 and which <c>-commandBuffer</c> BLOCKS at rather than failing. The observable
    /// end state is a frame loop that hangs on the 65th buffer with nothing reporting why, and
    /// <see cref="MetalUncommittedBuffers"/> is blind to it because the list counted that buffer as released.
    /// Ending an encoder on a buffer nobody will commit is one native call, and it buys the slot back, a driver
    /// left in a clean state, and one code path instead of three.</para>
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal readonly struct MetalEncoderSink : IMetalEncoderSink
    {
        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public IntPtr BeginRenderEncoder(IntPtr commandBuffer, IntPtr descriptor)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            return Retained(new MTLCommandBuffer(commandBuffer).RenderCommandEncoder(descriptor));
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public IntPtr BeginBlitEncoder(IntPtr commandBuffer)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            // .Handle because a list holds its encoder across calls and every transition here is by raw pointer.
            // The typed MTLBlitCommandEncoder exists for the setup batch, which opens, copies and ends in one go.
            return Retained(new MTLCommandBuffer(commandBuffer).BlitCommandEncoder().Handle);
        }

        /// <inheritdoc/>
        /// <remarks>SERIAL (M-H4), which is what makes a dependent dispatch inside one encoder ordered with no
        /// barrier machinery behind it.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public IntPtr BeginComputeEncoder(IntPtr commandBuffer)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            return Retained(new MTLCommandBuffer(commandBuffer).ComputeCommandEncoder(MTLDispatchType.Serial));
        }

        /// <inheritdoc/>
        /// <remarks>The end AND the release, in that order: releasing an encoder that has not been ended would
        /// leave the command buffer holding one it can never be told about, and the buffer could then never be
        /// committed.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void EndEncoding(MetalEncoderKind kind, IntPtr encoder)
        {
            if (encoder == IntPtr.Zero) return;

            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            new MTLCommandEncoder(encoder).EndEncoding();
            ObjCRuntime.ObjcRelease(encoder);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// THE STAGE PICKS THE ENCODER KIND AS WELL AS THE SELECTOR, which is why the fork is here rather than
        /// one level down. Compute's setters are unprefixed selectors on a DIFFERENT protocol
        /// (<see cref="MTLComputeCommandEncoder"/>), so this is not two spellings of one call: sending a
        /// <c>setVertexBuffers:</c> to a compute encoder is an unrecognised selector, and the flush that produced
        /// the bind already knows which encoder it is writing into.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetBuffers(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> buffers,
            ReadOnlySpan<nuint> offsets, uint firstIndex)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            if (stage == MetalShaderStage.Compute)
                new MTLComputeCommandEncoder(encoder).SetBuffers(buffers, offsets, firstIndex);
            else
                new MTLRenderCommandEncoder(encoder).SetBuffers(stage, buffers, offsets, firstIndex);
        }

        /// <inheritdoc/>
        /// <remarks>Same fork and same reason as <see cref="SetBuffers"/>.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetTextures(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> textures,
            uint firstIndex)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            if (stage == MetalShaderStage.Compute)
                new MTLComputeCommandEncoder(encoder).SetTextures(textures, firstIndex);
            else
                new MTLRenderCommandEncoder(encoder).SetTextures(stage, textures, firstIndex);
        }

        /// <inheritdoc/>
        /// <remarks>Same fork and same reason as <see cref="SetBuffers"/>.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetSamplerStates(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> samplers,
            uint firstIndex)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            if (stage == MetalShaderStage.Compute)
                new MTLComputeCommandEncoder(encoder).SetSamplerStates(samplers, firstIndex);
            else
                new MTLRenderCommandEncoder(encoder).SetSamplerStates(stage, samplers, firstIndex);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// THE HOTTEST MEMBER IN THIS TYPE, and it still opens a pool, because M-N5 is a rule without exceptions
        /// and <see cref="MetalRenderApi"/>'s two setters already pay the same for the same reason. None of the
        /// four setters here returns an autoreleased object, so the pool has nothing to drain: what it buys is
        /// that <c>MetalAutoreleaseArchitectureTests</c> can state the rule as "no path reaches a message send
        /// unpooled" with no exception list, and an exception list is the thing that rots. The cost is a push and
        /// a pop around one C call on the shadow pass's per-draw path, which is the one number in this backend
        /// worth measuring before anyone argues about it
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/600).
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetBufferOffset(MetalShaderStage stage, IntPtr encoder, nuint offset, uint index)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            if (stage == MetalShaderStage.Compute)
                new MTLComputeCommandEncoder(encoder).SetBufferOffset(offset, index);
            else
                new MTLRenderCommandEncoder(encoder).SetBufferOffset(stage, offset, index);
        }

        /// <inheritdoc/>
        public void Draw(IntPtr encoder, uint vertexStart, uint vertexCount, uint instanceCount,
            uint baseInstance)
            => throw NotBuiltYet("Drawing", DrawsRow);

        /// <inheritdoc/>
        public void DrawIndexed(IntPtr encoder, uint indexCount, IntPtr indexBuffer, nuint indexBufferOffset,
            bool sixteenBitIndices, uint instanceCount, int baseVertex, uint baseInstance)
            => throw NotBuiltYet("Drawing indexed", DrawsRow);

        /// <inheritdoc/>
        public void Dispatch(IntPtr encoder, uint groupCountX, uint groupCountY, uint groupCountZ,
            uint threadsPerGroupX, uint threadsPerGroupY, uint threadsPerGroupZ)
            => throw NotBuiltYet("Dispatching", DrawsRow);

        // The retain is what makes the encoder's lifetime this backend's rather than the caller's pool's. See the
        // class note: the alternative is a pool spanning a whole recording, which is the accumulation M-N5 exists
        // to prevent.
        static IntPtr Retained(IntPtr encoder)
            => encoder == IntPtr.Zero ? IntPtr.Zero : ObjCRuntime.ObjcRetain(encoder);

        const string DrawsRow = "the draw-and-dispatch row (https://github.com/APKiwiOrg/KhaozEngine/issues/580)";

        static NotSupportedException NotBuiltYet(string what, string row)
            => new($"{what} is not built yet on the native Metal backend: it lands in {row}, which is the row "
                + "that has a caller for it and the test that runs the Objective-C prototype it adds. The seam "
                + "itself covers all three of decision M-T2's call classes already, and TWO of the three emit: "
                + "the ENCODER BOUNDARY from work-breakdown row 7 "
                + "(https://github.com/APKiwiOrg/KhaozEngine/issues/573) and every ARGUMENT-TABLE write from row "
                + "13 (https://github.com/APKiwiOrg/KhaozEngine/issues/579). This is a statement about the "
                + "package and not about this machine.");
    }
}
