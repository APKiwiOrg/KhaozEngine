using System;
using System.Runtime.CompilerServices;
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
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11ShaderSet : ID3D11ShaderSet
    {
        readonly D3D11DeferredResource<D3D11CompiledPair, Native> _resource;

        internal D3D11ShaderSet(ID3D11Device device, DeviceLiveness liveness, D3D11CreationGate creation,
            D3D11ShaderCompilation<D3D11CompiledPair> compilation)
        {
            _resource = new(liveness, compilation.Join, compiled => CreateWindows(device, liveness, creation, compiled));
        }

        /// <inheritdoc/>
        public ID3D11VertexShader VertexShader => _resource.Value.Vertex;

        /// <inheritdoc/>
        public ID3D11PixelShader PixelShader => _resource.Value.Pixel;

        /// <inheritdoc/>
        public ReadOnlyMemory<byte> VertexShaderBytecode => _resource.Prepared.VertexDxbc;

        /// <summary>True once disposed, whether or not anything native was released.</summary>
        internal bool IsDisposed => _resource.IsDisposed;

        internal void EnsureReady() => _ = _resource.Value;

        public void Dispose() => _resource.Dispose();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static Native CreateWindows(ID3D11Device device, DeviceLiveness liveness, D3D11CreationGate creation,
            in D3D11CompiledPair compiled)
        {
            using var scope = creation.Enter();
            ObjectDisposedException.ThrowIf(liveness.IsDead, device);
            var vertex = device.CreateVertexShader(compiled.VertexDxbc);
            try { return new Native(vertex, device.CreatePixelShader(compiled.FragmentDxbc)); }
            catch
            {
                if (liveness.IsAlive) vertex.Dispose();
                throw;
            }
        }

        sealed class Native(ID3D11VertexShader vertex, ID3D11PixelShader pixel) : IDisposable
        {
            internal ID3D11VertexShader Vertex { get; } = vertex;
            internal ID3D11PixelShader Pixel { get; } = pixel;
            public void Dispose()
            {
                Vertex.Dispose();
                Pixel.Dispose();
            }
        }
    }
}
