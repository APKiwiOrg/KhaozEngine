using System;
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
    internal sealed class D3D11ComputeShader : IGpuComputeShader
    {
        readonly DeviceLiveness _liveness;
        readonly D3D11ShaderCompilation<D3D11PreparedCompute> _prepared;
        readonly D3D11DeferredResource<D3D11PreparedCompute, Native> _resource;

        internal D3D11ComputeShader(DeviceLiveness liveness, object shader, byte[] bytecode,
            uint threadGroupSizeX, uint threadGroupSizeY, uint threadGroupSizeZ, D3D11ShaderEvidence? diagnostics = null)
            : this(liveness, D3D11ShaderCompilation<D3D11PreparedCompute>.Completed(new(new(bytecode, threadGroupSizeX, threadGroupSizeY, threadGroupSizeZ), diagnostics)),
                _ => shader)
        {
            ArgumentNullException.ThrowIfNull(shader);
            ArgumentNullException.ThrowIfNull(bytecode);
            _ = _resource.Value;
        }

        internal D3D11ComputeShader(DeviceLiveness liveness, D3D11ShaderCompilation<D3D11PreparedCompute> prepared,
            Func<D3D11PreparedCompute, object> create)
        {
            ArgumentNullException.ThrowIfNull(liveness);
            _liveness = liveness;
            _prepared = prepared;
            _resource = new(liveness, _prepared.Join, p => new(create(p)));
        }

        internal D3D11ShaderCompilation<D3D11PreparedCompute> Preparation => _prepared;

        internal D3D11ShaderEvidence? DiagnosticEvidence => _prepared.Join().Evidence;
        internal object ShaderHandle => _resource.Value.Shader;
        internal bool DeviceIsDead => _liveness.IsDead;
        /// <summary>The compiled compute module, joined and realized at first use.</summary>
        [SupportedOSPlatform("windows")]
        internal ID3D11ComputeShader Shader => (ID3D11ComputeShader)ShaderHandle;
        internal ReadOnlyMemory<byte> Bytecode => _resource.Prepared.Code.Dxbc;
        public uint ThreadGroupSizeX => _resource.Prepared.Code.ThreadGroupSizeX;
        public uint ThreadGroupSizeY => _resource.Prepared.Code.ThreadGroupSizeY;
        public uint ThreadGroupSizeZ => _resource.Prepared.Code.ThreadGroupSizeZ;
        internal bool IsDisposed => _resource.IsDisposed;
        internal void EnsureReady() => _ = _resource.Value;
        public void Dispose() => _resource.Dispose();

        sealed class Native(object shader) : IDisposable
        {
            internal object Shader { get; } = shader;
            public void Dispose() => ((IDisposable)Shader).Dispose();
        }
    }
}
