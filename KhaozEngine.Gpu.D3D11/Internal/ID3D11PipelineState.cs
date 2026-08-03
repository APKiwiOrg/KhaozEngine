namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE SEVEN PIPELINE-LEVEL OBJECTS A GRAPHICS PIPELINE BINDS, exposed for the redundancy caches of decision
    /// R6 and for nothing else. A backend pipeline handle implements this alongside <see cref="IGpuPipeline"/>,
    /// and <see cref="D3D11DeviceState.BindPipeline"/> is the only consumer.
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
    /// EVERY PROPERTY MUST RETURN THE SAME INSTANCE ON EVERY READ, never a boxed value and never a wrapper built
    /// per access, because the cache compares by reference identity: a fresh object each read is never equal to
    /// the last one, so every bind reports a change, the whole R6 cache is defeated, and nothing throws or logs.
    /// </para>
    /// </summary>
    internal interface ID3D11PipelineState
    {
        /// <summary>The compiled vertex shader, bound with <c>VSSetShader</c>.</summary>
        object? VertexShader { get; }

        /// <summary>The compiled pixel shader, bound with <c>PSSetShader</c>.</summary>
        object? PixelShader { get; }

        /// <summary>
        /// The blend state object, bound with <c>OMSetBlendState</c>.
        /// <para>
        /// THE BLEND FACTOR IS NOT HERE YET, AND THE CACHE IS INCOMPLETE WITHOUT IT.
        /// <c>OMSetBlendState</c> takes a blend factor and a sample mask alongside the state object, and the
        /// blend factor rides the pipeline rather than being separately tracked state, so two pipelines that
        /// share one blend state object and differ in blend factor would take the redundant path here and the
        /// second one would draw with the first one's factor. Adding the factor to this interface and to the
        /// cache belongs with the draw path that owns the per-pipeline blend factor, which is issue #454, and
        /// must land with it.
        /// </para>
        /// </summary>
        object? BlendState { get; }

        /// <summary>The depth-stencil state object, bound with <c>OMSetDepthStencilState</c>. The stencil
        /// reference is the same open case as the blend factor above: it is an argument of the call rather than
        /// part of the state object, so it has to join the cache when the pipeline starts carrying it. Issue
        /// #454 is where the blend-factor half of that lands, on the draw path, and the stencil reference has
        /// the same shape.</summary>
        object? DepthStencilState { get; }

        /// <summary>The rasterizer state object, bound with <c>RSSetState</c>.</summary>
        object? RasterizerState { get; }

        /// <summary>The input layout, bound with <c>IASetInputLayout</c>.</summary>
        object? InputLayout { get; }

        /// <summary>The primitive topology as its <c>D3D_PRIMITIVE_TOPOLOGY</c> value, bound with
        /// <c>IASetPrimitiveTopology</c>. Zero is <c>UNDEFINED</c>, which is what an unbound context reports and
        /// therefore what a cache reset leaves behind.</summary>
        uint PrimitiveTopology { get; }
    }
}
