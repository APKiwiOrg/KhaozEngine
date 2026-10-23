using System;
using System.Runtime.Versioning;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu.Internal;
using Vortice.Direct3D11;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// The device-facing half of the shader path: turns the DXBC <see cref="D3D11ShaderBuild"/> produces into the
    /// Direct3D objects a pipeline binds. One per device, built by the resource factory.
    ///
    /// <para>
    /// THE TWO ENVIRONMENT LEVERS ARE READ ONCE, HERE, and that is deliberate. Compile flags
    /// (<see cref="D3D11ShaderDebug"/>) and the disk cache (<see cref="D3D11DxbcCache"/>) are properties of a
    /// SESSION, so re-reading them per shader would let a variable change halfway through a load and produce a
    /// device holding modules compiled two different ways, which is exactly the sort of state nobody thinks to
    /// suspect. Reading once also gives the diagnostic lines a single honest place to be emitted from: a run on
    /// the default says nothing, and a run on a lever says so once rather than thirty times.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11ShaderCompiler
    {
        static readonly ILogger log = Log.For<D3D11ShaderCompiler>();

        readonly ID3D11Device _device;
        readonly DeviceLiveness _liveness;
        readonly D3D11DxbcCache? _cache;
        readonly uint _flags;
        readonly D3D11CreationGate _creation;

        /// <summary>Builds the compiler for a device, reading the compile flags and the cache location off the
        /// live environment and reporting what it found.</summary>
        internal D3D11ShaderCompiler(ID3D11Device device, DeviceLiveness liveness, D3D11CreationGate creation)
            : this(device, liveness, D3D11DxbcCache.FromEnvironment(), ResolveFlags(), creation)
        {
        }

        /// <summary>The explicit form, for a test that wants a known cache and known flags rather than whatever
        /// the machine's environment says.</summary>
        internal D3D11ShaderCompiler(ID3D11Device device, DeviceLiveness liveness, D3D11DxbcCache? cache,
            uint flags, D3D11CreationGate creation)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(liveness);
            ArgumentNullException.ThrowIfNull(creation);

            _device = device;
            _liveness = liveness;
            _cache = cache;
            _flags = flags;
            _creation = creation;
        }

        /// <summary>The FXC flags every shader this compiler builds is compiled with.</summary>
        internal uint Flags => _flags;

        /// <summary>The disk cache in use, or null when it is off or the platform reports no cache location.
        /// </summary>
        internal D3D11DxbcCache? Cache => _cache;

        /// <summary>Queue a GLSL pair. Its first bytecode or native consumer joins the compile.</summary>
        /// <exception cref="ShaderValidationException">A source failed to compile, FXC rejected the emitted HLSL,
        /// or the vertex input signature is holed (decision S5).</exception>
        internal ID3D11ShaderSet CreateShaderSet(string vertexGlsl, string fragmentGlsl)
        {
            ArgumentNullException.ThrowIfNull(vertexGlsl);
            ArgumentNullException.ThrowIfNull(fragmentGlsl);
            uint flags = _flags;
            var cache = _cache;
            string? key = cache is null ? null : cache.Directory + "\0"
                + D3D11ShaderKey.For(D3D11ShaderStage.Vertex, flags, vertexGlsl, fragmentGlsl);
            // Copy only CPU inputs into the worker closure. It must not retain or touch the native device.
            var compiled = D3D11ShaderCompilation<D3D11CompiledPair>.Start(
                () => D3D11ShaderBuild.Pair(vertexGlsl, fragmentGlsl, flags, cache), key);
            return new D3D11ShaderSet(_device, _liveness, _creation, compiled);
        }

        /// <summary>Queue a compute source. Reading its workgroup size or creating a pipeline joins it.</summary>
        /// <exception cref="ShaderValidationException">The source failed to compile, declares no resolvable
        /// workgroup size, or FXC rejected the emitted HLSL.</exception>
        internal IGpuComputeShader CreateComputeShader(string computeGlsl)
        {
            ArgumentNullException.ThrowIfNull(computeGlsl);
            uint flags = _flags;
            var cache = _cache;
            string? key = cache is null ? null : cache.Directory + "\0"
                + D3D11ShaderKey.For(D3D11ShaderStage.Compute, flags, computeGlsl);
            var compiled = D3D11ShaderCompilation<D3D11CompiledCompute>.Start(
                () => D3D11ShaderBuild.Compute(computeGlsl, flags, cache), key);
            return new D3D11ComputeShader(_device, _liveness, _creation, compiled);
        }

        static uint ResolveFlags()
        {
            uint flags = D3D11ShaderDebug.FromEnvironment(out string? unrecognized);
            if (unrecognized is not null) log.Warn(D3D11ShaderDebug.UnrecognizedWarning(unrecognized));
            else if (flags != D3D11ShaderDebug.Optimized) log.Info(D3D11ShaderDebug.DebugDescription);
            return flags;
        }
    }
}
