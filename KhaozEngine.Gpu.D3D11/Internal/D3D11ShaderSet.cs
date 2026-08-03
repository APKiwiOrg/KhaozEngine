using System;
using System.Runtime.Versioning;
using Vortice.Direct3D11;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// <see cref="ID3D11ShaderSet"/> for the native Direct3D 11 backend: one compiled vertex and pixel shader
    /// pair, plus the vertex DXBC the input layout is validated against.
    /// <para>
    /// THE BYTECODE IS KEPT ALIVE ON PURPOSE, and it is the one field here that is not obviously necessary.
    /// Direct3D validates an input layout against a real compiled vertex shader signature at creation, so the
    /// bytes have to still be in hand every time a pipeline is built from this set, which is any number of times
    /// and at any point after the compile. The engine's post-process path alone builds nine pipelines from a
    /// memoized handful of sets. Holding roughly a few kilobytes per program for the process lifetime is the
    /// cheaper half of that trade by a wide margin.
    /// </para>
    /// <para>
    /// Disposal is gated on device liveness like every other resource in this package (decision X3): after the
    /// device dies the runtime has already freed every child object, so releasing one again is what turns a
    /// teardown-order straggler into a crash.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11ShaderSet : ID3D11ShaderSet
    {
        readonly D3D11DeviceLiveness _liveness;

        internal D3D11ShaderSet(D3D11DeviceLiveness liveness, ID3D11VertexShader vertexShader,
            ID3D11PixelShader pixelShader, byte[] vertexShaderBytecode)
        {
            ArgumentNullException.ThrowIfNull(liveness);
            ArgumentNullException.ThrowIfNull(vertexShader);
            ArgumentNullException.ThrowIfNull(pixelShader);
            ArgumentNullException.ThrowIfNull(vertexShaderBytecode);

            _liveness = liveness;
            VertexShader = vertexShader;
            PixelShader = pixelShader;
            VertexShaderBytecode = vertexShaderBytecode;
        }

        /// <inheritdoc/>
        public ID3D11VertexShader VertexShader { get; }

        /// <inheritdoc/>
        public ID3D11PixelShader PixelShader { get; }

        /// <inheritdoc/>
        public ReadOnlyMemory<byte> VertexShaderBytecode { get; }

        /// <summary>True once disposed, whether or not anything native was released.</summary>
        internal bool IsDisposed { get; private set; }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            if (_liveness.IsDead) return;   // the device already freed every child object

            VertexShader.Dispose();
            PixelShader.Dispose();
        }
    }
}
