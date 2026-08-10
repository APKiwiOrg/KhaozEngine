using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE REAL <see cref="IMetalEncoderSink"/>: the emitting half of M-T2's budget seam, over the interop layer.
    ///
    /// <para><b>WHAT THIS ROW EMITS IS THE ENCODER BOUNDARY, AND THE OTHER TWO CLASSES LAND WITH THEIR
    /// CALLERS.</b> The SEAM covers all three call classes and is complete here, because a budget seam
    /// retrofitted after the recorder exists is a seam shaped by the recorder rather than by what needs counting,
    /// and phase 2 records exactly that outcome. The EMISSIONS are a different question: a native prototype added
    /// by a row that has no caller for it and no test that runs it is an Objective-C declaration nobody has ever
    /// executed, and row 1's own regression evidence is that a wrong ABI assumption in interop is a memory
    /// corruption rather than a compile error. So the argument-table setters emit when row 13 flushes through
    /// them (https://github.com/APKiwiOrg/KhaozEngine/issues/579) and the draws when row 14 issues them
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580), each with the test that runs the prototype it
    /// adds. Until then they refuse by name, in the same shape the command list's unbuilt members take.</para>
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
            return Retained(new MTLCommandBuffer(commandBuffer).BlitCommandEncoder());
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
        public void SetBuffers(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> buffers,
            ReadOnlySpan<nuint> offsets, uint firstIndex)
            => throw NotBuiltYet("The array buffer setter", BindsRow);

        /// <inheritdoc/>
        public void SetTextures(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> textures,
            uint firstIndex)
            => throw NotBuiltYet("The array texture setter", BindsRow);

        /// <inheritdoc/>
        public void SetSamplerStates(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> samplers,
            uint firstIndex)
            => throw NotBuiltYet("The array sampler setter", BindsRow);

        /// <inheritdoc/>
        public void SetBufferOffset(MetalShaderStage stage, IntPtr encoder, nuint offset, uint index)
            => throw NotBuiltYet("The offsets-only rebind", BindsRow);

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

        const string BindsRow = "the bind-flush row (https://github.com/APKiwiOrg/KhaozEngine/issues/579)";
        const string DrawsRow = "the draw-and-dispatch row (https://github.com/APKiwiOrg/KhaozEngine/issues/580)";

        static NotSupportedException NotBuiltYet(string what, string row)
            => new($"{what} is not built yet on the native Metal backend: it lands in {row}, which is the row "
                + "that has a caller for it and the test that runs the Objective-C prototype it adds. The seam "
                + "itself covers all three of decision M-T2's call classes already, and the ENCODER BOUNDARY "
                + "half of it is live (work-breakdown row 7, "
                + "https://github.com/APKiwiOrg/KhaozEngine/issues/573). This is a statement about the package "
                + "and not about this machine.");
    }
}
