using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Internal;
using Vortice.Direct3D11;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// <see cref="IGpuComputeShader"/> for the native Direct3D 11 backend: one compiled compute module, its DXBC,
    /// and the workgroup size read out of the SPIR-V.
    ///
    /// <para>
    /// WHY THE WORKGROUP SIZE IS CARRIED AT ALL, when Direct3D does not need it. D3D11 takes the thread-group
    /// size from the module itself, exactly as Vulkan does, so <c>Dispatch</c> here never names it. The seam
    /// exposes it anyway (<see cref="IGpuComputeShader.ThreadGroupSizeX"/> and siblings) because a CALLER needs it
    /// to work out how many groups cover N elements, and because Metal genuinely does need it at dispatch. So
    /// <c>SpirvLocalSize</c> keeps hand-parsing the one execution mode out of the module on this backend too,
    /// unchanged, and the number a caller reads is the number the shader declared rather than a copy someone kept
    /// in sync. Decision S1 says so in as many words.
    /// </para>
    /// <para>
    /// THE BYTECODE IS KEPT for the compute PIPELINE work that follows in
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/456, which is the row that builds dispatch. Nothing in this
    /// row reads it, and it is here rather than there because the bytes exist only at compile time and holding
    /// them costs a few kilobytes. This is the whole forward declaration that row needs from the shader path: a
    /// compute pipeline binds <see cref="Shader"/> and asks nothing else of a compiled module.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11ComputeShader : IGpuComputeShader
    {
        readonly D3D11ShaderResource<D3D11CompiledCompute, ID3D11ComputeShader> _resource;

        internal D3D11ComputeShader(ID3D11Device device, DeviceLiveness liveness, D3D11CreationGate creation,
            D3D11ShaderCompilation<D3D11CompiledCompute> compilation)
        {
            _resource = new(liveness, compilation, compiled => CreateWindows(device, creation, compiled));
        }

        /// <summary>The compiled compute module, which a compute pipeline binds.</summary>
        internal ID3D11ComputeShader Shader => _resource.Value;

        /// <summary>The module's DXBC bytes.</summary>
        internal ReadOnlyMemory<byte> Bytecode => _resource.Compiled.Dxbc;

        /// <inheritdoc/>
        public uint ThreadGroupSizeX => _resource.Compiled.ThreadGroupSizeX;

        /// <inheritdoc/>
        public uint ThreadGroupSizeY => _resource.Compiled.ThreadGroupSizeY;

        /// <inheritdoc/>
        public uint ThreadGroupSizeZ => _resource.Compiled.ThreadGroupSizeZ;

        /// <summary>True once disposed, whether or not anything native was released.</summary>
        internal bool IsDisposed => _resource.IsDisposed;

        internal void EnsureReady() => _ = _resource.Value;

        public void Dispose() => _resource.Dispose();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static ID3D11ComputeShader CreateWindows(ID3D11Device device, D3D11CreationGate creation,
            in D3D11CompiledCompute compiled)
        {
            using var scope = creation.Enter();
            return device.CreateComputeShader(compiled.Dxbc);
        }
    }
}
