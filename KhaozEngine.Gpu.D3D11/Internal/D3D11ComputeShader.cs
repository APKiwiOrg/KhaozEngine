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
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11ComputeShader : IGpuComputeShader
    {
        readonly DeviceLiveness _liveness;

        internal D3D11ComputeShader(DeviceLiveness liveness, ID3D11ComputeShader shader, byte[] bytecode,
            uint threadGroupSizeX, uint threadGroupSizeY, uint threadGroupSizeZ)
        {
            ArgumentNullException.ThrowIfNull(liveness);
            ArgumentNullException.ThrowIfNull(shader);
            ArgumentNullException.ThrowIfNull(bytecode);

            _liveness = liveness;
            Shader = shader;
            Bytecode = bytecode;
            ThreadGroupSizeX = threadGroupSizeX;
            ThreadGroupSizeY = threadGroupSizeY;
            ThreadGroupSizeZ = threadGroupSizeZ;
        }

        /// <summary>The compiled compute module, which a compute pipeline binds.</summary>
        internal ID3D11ComputeShader Shader { get; }

        /// <summary>The module's DXBC bytes.</summary>
        internal ReadOnlyMemory<byte> Bytecode { get; }

        /// <inheritdoc/>
        public uint ThreadGroupSizeX { get; }

        /// <inheritdoc/>
        public uint ThreadGroupSizeY { get; }

        /// <inheritdoc/>
        public uint ThreadGroupSizeZ { get; }

        /// <summary>True once disposed, whether or not anything native was released.</summary>
        internal bool IsDisposed { get; private set; }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            if (_liveness.IsDead) return;   // the device already freed every child object

            Shader.Dispose();
        }
    }
}
