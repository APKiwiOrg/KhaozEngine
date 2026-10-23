using System;
using Vortice.Direct3D11;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// What a graphics pipeline needs out of a compiled vertex and fragment pair. Declared here, separately from
    /// the shader path that produces it. Pipeline plans retain this interface without reading it until first
    /// bind, so constructing a batch never joins its programs one by one.
    /// <para>
    /// THE BYTECODE IS THE REASON THIS EXISTS. Direct3D 11 validates an input layout against a REAL compiled vertex
    /// shader signature at native layout creation, so first bind joins the retained shader before creating state.
    /// Handing the pipeline the bytes rather than
    /// letting it ask a compiler for them keeps the shader path out of the pipeline entirely.
    /// </para>
    /// <para>
    /// The shader path owns the implementation, including the FXC call, the disk cache and the reflected-signature
    /// contiguity assertion that the two documented WARP corruption incidents made necessary.
    /// </para>
    /// </summary>
    internal interface ID3D11ShaderSet : IGpuShaderSet
    {
        /// <summary>The compiled vertex shader.</summary>
        ID3D11VertexShader VertexShader { get; }

        /// <summary>The compiled pixel shader.</summary>
        ID3D11PixelShader PixelShader { get; }

        /// <summary>The vertex shader's DXBC bytes, which the input layout is validated against.</summary>
        ReadOnlyMemory<byte> VertexShaderBytecode { get; }
    }
}
