using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>One compiled stage on the device: its library, its entry-point function, and which stage it
    /// is.</summary>
    /// <param name="Stage">Which stage.</param>
    /// <param name="Library">The <c>MTLLibrary</c> this stage's MSL compiled to, at +1.</param>
    /// <param name="Function">The entry-point <c>MTLFunction</c> inside it, at +1.</param>
    internal readonly record struct MetalCompiledStage(
        MetalShaderStage Stage, MTLLibrary Library, MTLFunction Function);

    /// <summary>
    /// The seam's <see cref="IGpuShaderSet"/>: a vertex and fragment pair as compiled Metal functions, plus the
    /// binding table read off the emission they were compiled from.
    ///
    /// <para>
    /// ONE LIBRARY PER STAGE, WHICH IS FORCED RATHER THAN CHOSEN. SPIRV-Cross emits each stage as its own
    /// translation unit and names BOTH entry points <c>main0</c>, so compiling the two texts into one library is
    /// a duplicate-symbol error. The device measured it: <c>definition with same mangled name 'main0' as another
    /// definition</c>. The incumbent has the same shape for the same reason.
    /// </para>
    /// <para>
    /// THE TABLE TRAVELS WITH THE SHADER SET rather than being rebuilt at pipeline creation, because it is a
    /// property of the EMISSION and the emission happens here. Row 10
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/576) content-deduplicates it and hangs it off the
    /// pipeline, and row 11 (https://github.com/APKiwiOrg/KhaozEngine/issues/577) calls
    /// <c>MetalShaderIndexTable.RequireLayoutShape</c> against the layout array the pipeline declares. Neither
    /// re-reads the MSL.
    /// </para>
    /// </summary>
    internal sealed class MetalShaderSet : IGpuShaderSet, IMetalOwnedResource
    {
        readonly IMetalDeviceLiveness _liveness;
        readonly MetalCompiledStage[] _stages;

        bool _disposed;

        internal MetalShaderSet(IMetalDeviceLiveness liveness, MetalCompiledStage[] stages,
            MetalShaderIndexTable table)
        {
            _liveness = liveness;
            _stages = stages;
            Table = table;
        }

        /// <inheritdoc/>
        public IMetalDeviceLiveness Owner => _liveness;

        /// <summary>Where the emission put each declared element, per stage (M-B1). Read by rows 10, 11 and
        /// 13.</summary>
        internal MetalShaderIndexTable Table { get; }

        /// <summary>Every compiled stage, for the pipeline descriptor. Empty after disposal.</summary>
        internal IReadOnlyList<MetalCompiledStage> Stages => _disposed ? Array.Empty<MetalCompiledStage>() : _stages;

        /// <summary>The function for one stage, which is what a render pipeline descriptor's
        /// <c>vertexFunction</c> and <c>fragmentFunction</c> are set to.</summary>
        /// <exception cref="InvalidOperationException">This set carries no such stage, or it is disposed.</exception>
        internal MTLFunction FunctionFor(MetalShaderStage stage)
        {
            if (_disposed)
            {
                throw new InvalidOperationException(
                    "This native Metal shader set is disposed, so its functions are released. A pipeline created "
                    + "from one would hold a function the device has already let go of.");
            }

            foreach (MetalCompiledStage compiled in _stages)
                if (compiled.Stage == stage) return compiled.Function;

            throw new InvalidOperationException(
                $"This native Metal shader set carries no {stage.ToString().ToLowerInvariant()} stage.");
        }

        /// <summary>Release every function and library, once, and never on a dead device (M-F6).</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_liveness.IsDead) return;
            if (!KhaozEngineMetal.IsPlatformSupported) return;

            ReleaseOnMacOs();
        }

        // The FUNCTION goes before its library, which is the order the two were created in reversed. A library
        // keeps its functions alive by itself, so either order is legal, and doing it in this one keeps the rule
        // "release what you took, innermost first" true everywhere in this package rather than nearly everywhere.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ReleaseOnMacOs()
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            foreach (MetalCompiledStage stage in _stages)
            {
                stage.Function.Release();
                stage.Library.Release();
            }
        }
    }

    /// <summary>
    /// The seam's <see cref="IGpuComputeShader"/>: one compiled kernel plus the workgroup size read out of its
    /// SPIR-V.
    /// <para>
    /// THE SIZE COMES OFF THE MODULE AND NOT OFF A DESCRIPTION, unchanged from every other backend
    /// (<c>SpirvLocalSize</c>). MSL does not carry the workgroup size the way SPIR-V does, and Metal needs those
    /// exact numbers for <c>dispatchThreadgroups</c>'s <c>threadsPerThreadgroup</c>, so the module is the only
    /// honest source. The incumbent takes them from <c>ComputePipelineDescription.ThreadGroupSize*</c>, which
    /// validates nothing against the shader and is the silent-failure shape <c>SpirvLocalSize</c>'s own header
    /// records.
    /// </para>
    /// </summary>
    internal sealed class MetalComputeShader : IGpuComputeShader, IMetalOwnedResource
    {
        readonly IMetalDeviceLiveness _liveness;
        readonly MetalCompiledStage _stage;

        bool _disposed;

        internal MetalComputeShader(IMetalDeviceLiveness liveness, MetalCompiledStage stage,
            MetalShaderIndexTable table, uint x, uint y, uint z)
        {
            _liveness = liveness;
            _stage = stage;
            Table = table;
            ThreadGroupSizeX = x;
            ThreadGroupSizeY = y;
            ThreadGroupSizeZ = z;
        }

        /// <inheritdoc/>
        public uint ThreadGroupSizeX { get; }

        /// <inheritdoc/>
        public uint ThreadGroupSizeY { get; }

        /// <inheritdoc/>
        public uint ThreadGroupSizeZ { get; }

        /// <inheritdoc/>
        public IMetalDeviceLiveness Owner => _liveness;

        /// <summary>Where the emission put each declared element for the compute stage (M-B1).</summary>
        internal MetalShaderIndexTable Table { get; }

        /// <summary>The kernel function, which is what a compute pipeline is created from.</summary>
        /// <exception cref="InvalidOperationException">This shader is disposed.</exception>
        internal MTLFunction Function => _disposed
            ? throw new InvalidOperationException(
                "This native Metal compute shader is disposed, so its function is released.")
            : _stage.Function;

        /// <summary>Release the function and its library, once, and never on a dead device (M-F6).</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_liveness.IsDead) return;
            if (!KhaozEngineMetal.IsPlatformSupported) return;

            ReleaseOnMacOs();
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ReleaseOnMacOs()
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            _stage.Function.Release();
            _stage.Library.Release();
        }
    }
}
