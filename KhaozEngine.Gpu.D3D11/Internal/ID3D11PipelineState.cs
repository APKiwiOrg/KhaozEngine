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
    /// </summary>
    internal interface ID3D11PipelineState
    {
        /// <summary>The compiled vertex shader, bound with <c>VSSetShader</c>.</summary>
        object? VertexShader { get; }

        /// <summary>The compiled pixel shader, bound with <c>PSSetShader</c>.</summary>
        object? PixelShader { get; }

        /// <summary>The blend state object, bound with <c>OMSetBlendState</c>.</summary>
        object? BlendState { get; }

        /// <summary>The depth-stencil state object, bound with <c>OMSetDepthStencilState</c>.</summary>
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
