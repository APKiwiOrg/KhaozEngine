using System.Numerics;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// WHAT A GRAPHICS PIPELINE BINDS, exposed for the redundancy caches of decision R6 and for nothing else. A
    /// backend pipeline handle implements this alongside <see cref="IGpuPipeline"/>, and
    /// <see cref="D3D11DeviceState.BindPipeline"/> is the only consumer.
    /// <para>
    /// EVERY OBJECT IS TYPED <c>object</c> HERE, deliberately. A redundancy cache asks one question, which is
    /// whether the same instance is already bound, and reference identity answers it without this file naming a
    /// Direct3D type. That is what keeps the cache, its tests and this whole seam device-free on macOS and Linux,
    /// which is the property the counting emitter and every device-free <c>[Fact]</c> in this package rest on.
    /// </para>
    /// <para>
    /// The untyped shape costs the real emitter nothing, because the emitter already casts the seam's
    /// <see cref="IGpuPipeline"/> to its own concrete pipeline type and reads its TYPED fields to make the call.
    /// This interface answers "what changed", the concrete type answers "with what", and neither has to downcast
    /// per bind. An interface of typed Direct3D handles would have put a Vortice type in the signature of the one
    /// type the device-free tests drive hardest.
    /// </para>
    /// <para>
    /// EVERY OBJECT PROPERTY MUST RETURN THE SAME INSTANCE ON EVERY READ, never a boxed value and never a wrapper
    /// built per access, because the cache compares by reference identity: a fresh object each read is never equal
    /// to the last one, so every bind reports a change, the whole R6 cache is defeated, and nothing throws or logs.
    /// The VALUE members below are compared by value instead and carry no such rule, which is exactly why they are
    /// values here rather than boxed objects in the same array.
    /// </para>
    /// </summary>
    internal interface ID3D11PipelineState
    {
        /// <summary>The compiled vertex shader, bound with <c>VSSetShader</c>.</summary>
        object? VertexShader { get; }

        /// <summary>The compiled pixel shader, bound with <c>PSSetShader</c>.</summary>
        object? PixelShader { get; }

        /// <summary>
        /// The blend state object, bound with <c>OMSetBlendState</c> together with
        /// <see cref="BlendFactor"/>.
        /// <para>
        /// THE OBJECT ALONE IS NOT THE CACHE KEY, and that is issue #454's decision rather than an implementation
        /// detail of the caller. <c>OMSetBlendState</c> takes the state object, a blend FACTOR and a sample mask,
        /// and the factor rides the pipeline (5.3) rather than being separately tracked state, so two pipelines
        /// that share one blend state object and differ only in factor would take R6's redundant path and the
        /// second would draw with the first one's factor. Golden visible and silent. The key is therefore the PAIR
        /// (object, factor), compared in <see cref="D3D11DeviceState.BindBlendState"/>.
        /// </para>
        /// <para>
        /// The SAMPLE MASK is deliberately NOT part of the key. It is not on the GPU seam at all, so every pipeline
        /// this backend builds passes the same constant, and a key member that cannot differ is a compare that can
        /// never decide anything. The day a mask reaches the seam it joins this interface and the key together,
        /// the way the factor just did.
        /// </para>
        /// </summary>
        object? BlendState { get; }

        /// <summary>
        /// The constant blend factor <c>OMSetBlendState</c> is issued with, and the second half of the blend cache
        /// key. Compared by VALUE, so two pipelines sharing a blend state object still rebind when their factors
        /// differ. A component that is NaN never compares equal to itself, so a NaN factor re-issues the bind every
        /// time rather than sticking: that is a caller defect costing one native call per bind, which is the safe
        /// direction to fail in.
        /// </summary>
        Vector4 BlendFactor { get; }

        /// <summary>
        /// The depth-stencil state object, bound with <c>OMSetDepthStencilState</c> together with
        /// <see cref="StencilReference"/>. Keyed on the PAIR for the same reason the blend state is: the reference
        /// is an argument of the call rather than part of the state object, so two pipelines sharing one object and
        /// differing in reference must not take the redundant path.
        /// </summary>
        object? DepthStencilState { get; }

        /// <summary>
        /// The stencil reference <c>OMSetDepthStencilState</c> is issued with, and the second half of the
        /// depth-stencil cache key.
        /// <para>
        /// EVERY SHIPPED PIPELINE ANSWERS ZERO TODAY, because the GPU seam carries no stencil state at all
        /// (<c>GpuDepthStencilState</c> is depth only, and <c>D3D11GraphicsPipeline</c> builds its state objects
        /// with <c>StencilEnable = false</c>). It is keyed anyway, and that is the point of doing it in the same
        /// decision as the blend factor: the hazard is identical in shape, it is invisible until a stencil pass
        /// exists, and the day the seam grows one the cache is already correct rather than silently wrong for a
        /// release. The cost is one <c>uint</c> compare per pipeline bind.
        /// </para>
        /// </summary>
        uint StencilReference { get; }

        /// <summary>The rasterizer state object, bound with <c>RSSetState</c>.</summary>
        object? RasterizerState { get; }

        /// <summary>The input layout, bound with <c>IASetInputLayout</c>.</summary>
        object? InputLayout { get; }

        /// <summary>The primitive topology as its <c>D3D_PRIMITIVE_TOPOLOGY</c> value, bound with
        /// <c>IASetPrimitiveTopology</c>. Zero is <c>UNDEFINED</c>, which is what an unbound context reports and
        /// therefore what a cache reset leaves behind.</summary>
        uint PrimitiveTopology { get; }

        /// <summary>
        /// PER-SLOT VERTEX STRIDE, indexed by vertex-buffer slot, and an INPUT to the vertex bind rather than an
        /// object the pipeline binds. It is on this interface because the stride is what makes a vertex bind
        /// pipeline-dependent: <c>IASetVertexBuffers</c> takes the stride alongside the buffer and the offset, so
        /// the same buffer at the same slot under two pipelines with different strides is two different binds.
        /// <para>
        /// <see cref="D3D11DeviceState"/> therefore adopts this array at every pipeline bind and, when the array
        /// is not the same instance, marks every bound vertex slot dirty so the next draw re-issues them. Without
        /// that a pipeline switch between two vertex formats would draw the second pass with the first pass's
        /// stride, which is garbage geometry with nothing thrown and nothing logged. Identity is taken on the
        /// ARRAY, so two pipelines sharing one stride array invalidate nothing, and a pipeline must answer the
        /// same instance on every read for the same reason the object members must.
        /// </para>
        /// </summary>
        uint[] VertexStrides { get; }
    }

    /// <summary>
    /// WHAT A COMPUTE PIPELINE BINDS, which is one shader. The compute sibling of
    /// <see cref="ID3D11PipelineState"/>, kept separate because a compute pipeline answers none of the seven
    /// graphics members and a graphics pipeline answers no compute shader.
    /// <para>
    /// Typed <c>object</c> for the same reason every member of the graphics seam is: it keeps the emitter's
    /// compute path expressible without a Direct3D type in this file, so the shape can be driven by a device-free
    /// test. There is deliberately no redundancy cache behind it yet. Caching the compute shader belongs with the
    /// rest of decision C1's compute schedule, which is work-breakdown row 12, and caching it here alone would be
    /// half a rule.
    /// </para>
    /// </summary>
    internal interface ID3D11ComputePipelineState
    {
        /// <summary>The compiled compute shader, bound with <c>CSSetShader</c>.</summary>
        object? ComputeShader { get; }
    }
}
