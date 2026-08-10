using System;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE STRUCT THAT CARRIES A BOXED <see cref="IMetalEncoderSink"/> INTO A GENERIC BODY, and it exists so the
    /// per-draw path can be written ONCE while still being monomorphized for the sink that matters.
    ///
    /// <para><b>THE PROBLEM IT SOLVES.</b> Section 6.4 wants the per-draw classes consumed through
    /// <c>where TSink : struct, IMetalEncoderSink</c> so the JIT monomorphizes them, and it also rules that the
    /// command list must NOT be generic over its sink, because that would put the sink type in the signature of
    /// every type that holds a list. Those two together mean the list holds the sink boxed and has to get an
    /// UNBOXED one back at each draw. For the real sink that is a type test, which is free: <c>MetalEncoderSink</c>
    /// is a readonly struct with no state at all. For every other implementation (the counting fake the
    /// device-free budget rows drive, and the logging one the device rows read their executed selector set off)
    /// there is no concrete type the shipped code can name, so this relay is what the generic body binds to.</para>
    ///
    /// <para><b>SO THE COST LANDS EXACTLY WHERE IT DOES NOT MATTER.</b> On the shipped path the type test hits,
    /// <c>TSink</c> is <c>MetalEncoderSink</c>, and there is no interface dispatch anywhere in a draw. On a test
    /// path <c>TSink</c> is this, and each emission costs one virtual call on top of a fake that is already
    /// writing to a list. The alternative shapes were both worse: making the list generic is the thing 6.4
    /// forbids, and dropping the type test so everything goes through this relay would put a virtual call per
    /// EMISSION on the shipped hot path, which is precisely the cost the struct constraint exists to avoid.</para>
    ///
    /// <para><b>IT IS A READONLY STRUCT WHOSE STATE IS A CLASS REFERENCE</b>, which is the emitter rule
    /// <see cref="IMetalEncoderSink"/> states, and it holds here for the same reason it holds for every other
    /// implementation: the inner sink is reached boxed on the boundary path and through this on the draw path, so
    /// a mutable field would count boundaries into one copy and draws into the other.</para>
    /// </summary>
    /// <param name="inner">The sink the list was built with.</param>
    internal readonly struct MetalRelayEncoderSink(IMetalEncoderSink inner) : IMetalEncoderSink
    {
        readonly IMetalEncoderSink _inner = inner;

        /// <inheritdoc/>
        public IntPtr BeginRenderEncoder(IntPtr commandBuffer, IntPtr descriptor)
            => _inner.BeginRenderEncoder(commandBuffer, descriptor);

        /// <inheritdoc/>
        public IntPtr BeginBlitEncoder(IntPtr commandBuffer) => _inner.BeginBlitEncoder(commandBuffer);

        /// <inheritdoc/>
        public IntPtr BeginComputeEncoder(IntPtr commandBuffer) => _inner.BeginComputeEncoder(commandBuffer);

        /// <inheritdoc/>
        public void EndEncoding(MetalEncoderKind kind, IntPtr encoder) => _inner.EndEncoding(kind, encoder);

        /// <inheritdoc/>
        public void SetBuffers(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> buffers,
            ReadOnlySpan<nuint> offsets, uint firstIndex)
            => _inner.SetBuffers(stage, encoder, buffers, offsets, firstIndex);

        /// <inheritdoc/>
        public void SetTextures(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> textures,
            uint firstIndex)
            => _inner.SetTextures(stage, encoder, textures, firstIndex);

        /// <inheritdoc/>
        public void SetSamplerStates(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> samplers,
            uint firstIndex)
            => _inner.SetSamplerStates(stage, encoder, samplers, firstIndex);

        /// <inheritdoc/>
        public void SetBufferOffset(MetalShaderStage stage, IntPtr encoder, nuint offset, uint index)
            => _inner.SetBufferOffset(stage, encoder, offset, index);

        /// <inheritdoc/>
        public void Draw(IntPtr encoder, MTLPrimitiveType topology, uint vertexStart, uint vertexCount,
            uint instanceCount, uint baseInstance)
            => _inner.Draw(encoder, topology, vertexStart, vertexCount, instanceCount, baseInstance);

        /// <inheritdoc/>
        public void DrawIndexed(IntPtr encoder, MTLPrimitiveType topology, uint indexCount, IntPtr indexBuffer,
            nuint indexBufferOffset, bool sixteenBitIndices, uint instanceCount, int baseVertex,
            uint baseInstance)
            => _inner.DrawIndexed(encoder, topology, indexCount, indexBuffer, indexBufferOffset,
                sixteenBitIndices, instanceCount, baseVertex, baseInstance);

        /// <inheritdoc/>
        public void Dispatch(IntPtr encoder, uint groupCountX, uint groupCountY, uint groupCountZ,
            uint threadsPerGroupX, uint threadsPerGroupY, uint threadsPerGroupZ)
            => _inner.Dispatch(encoder, groupCountX, groupCountY, groupCountZ, threadsPerGroupX,
                threadsPerGroupY, threadsPerGroupZ);
    }
}
