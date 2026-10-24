using System;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Internal;
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
    internal sealed class D3D11ShaderSet : ID3D11ShaderSet
    {
        readonly DeviceLiveness _liveness;
        readonly D3D11ShaderCompilation<D3D11PreparedGraphics> _prepared;
        readonly D3D11DeferredResource<D3D11PreparedGraphics, Native> _resource;

        internal D3D11ShaderSet(DeviceLiveness liveness, object vertexShader,
            object pixelShader, byte[] vertexShaderBytecode, D3D11ShaderEvidence? vertexEvidence = null,
            D3D11ShaderEvidence? pixelEvidence = null)
            : this(liveness, D3D11ShaderCompilation<D3D11PreparedGraphics>.Completed(new(new(vertexShaderBytecode, []), vertexEvidence, pixelEvidence)),
                _ => new(vertexShader, pixelShader))
        {
            ArgumentNullException.ThrowIfNull(vertexShader);
            ArgumentNullException.ThrowIfNull(pixelShader);
            ArgumentNullException.ThrowIfNull(vertexShaderBytecode);
            _ = _resource.Value;
        }

        internal D3D11ShaderSet(DeviceLiveness liveness, D3D11ShaderCompilation<D3D11PreparedGraphics> prepared,
            Func<D3D11PreparedGraphics, Native> create)
        {
            ArgumentNullException.ThrowIfNull(liveness);
            _liveness = liveness;
            _prepared = prepared;
            _resource = new(liveness, _prepared.Join, create);
        }

        internal D3D11ShaderCompilation<D3D11PreparedGraphics> Preparation => _prepared;

        internal object VertexHandle => _resource.Value.Vertex;
        internal object PixelHandle => _resource.Value.Pixel;
        internal D3D11ShaderEvidence? VertexEvidence => _prepared.Join().Vertex;
        internal D3D11ShaderEvidence? PixelEvidence => _prepared.Join().Pixel;
        internal bool DeviceIsDead => _liveness.IsDead;
        /// <inheritdoc/>
        public ID3D11VertexShader VertexShader => (ID3D11VertexShader)VertexHandle;
        /// <inheritdoc/>
        public ID3D11PixelShader PixelShader => (ID3D11PixelShader)PixelHandle;
        /// <inheritdoc/>
        public ReadOnlyMemory<byte> VertexShaderBytecode => _resource.Prepared.Code.VertexDxbc;
        internal bool IsDisposed => _resource.IsDisposed;
        internal void EnsureReady() => _ = _resource.Value;
        public void Dispose() => _resource.Dispose();

        internal sealed class Native(object vertex, object pixel) : IDisposable
        {
            internal object Vertex { get; } = vertex;
            internal object Pixel { get; } = pixel;
            public void Dispose()
            {
                ((IDisposable)Vertex).Dispose();
                ((IDisposable)Pixel).Dispose();
            }
        }
    }
}
