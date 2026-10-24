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
    internal sealed class D3D11ShaderCompiler
    {
        static readonly ILogger log = Log.For<D3D11ShaderCompiler>();

        readonly ID3D11ShaderCompilerApi _native;
        readonly DeviceLiveness _liveness;
        readonly D3D11DxbcCache? _cache;
        readonly uint _flags;
        readonly D3D11DiagnosticOptions _diagnostics;
        readonly ID3D11ShaderReflectionApi _reflection;

        /// <summary>Builds the compiler for a device, reading the compile flags and the cache location off the
        /// live environment and reporting what it found.</summary>
        [SupportedOSPlatform("windows")]
        internal D3D11ShaderCompiler(ID3D11Device device, DeviceLiveness liveness, D3D11DiagnosticOptions? diagnostics = null)
            : this(device, liveness, D3D11DxbcCache.FromEnvironment(), ResolveFlags(), diagnostics)
        {
        }

        /// <summary>The explicit form, for a test that wants a known cache and known flags rather than whatever
        /// the machine's environment says.</summary>
        [SupportedOSPlatform("windows")]
        internal D3D11ShaderCompiler(ID3D11Device device, DeviceLiveness liveness, D3D11DxbcCache? cache,
            uint flags, D3D11DiagnosticOptions? diagnostics = null, ID3D11ShaderReflectionApi? reflection = null)
            : this(liveness, new D3D11ShaderCompilerApi(device), cache, flags,
                diagnostics ?? new(false, D3D11RecordMode.Deferred, false, false), reflection ?? new D3D11ShaderReflectionApi())
        { ArgumentNullException.ThrowIfNull(device); }

        internal D3D11ShaderCompiler(DeviceLiveness liveness, ID3D11ShaderCompilerApi native, D3D11DxbcCache? cache,
            uint flags, D3D11DiagnosticOptions diagnostics, ID3D11ShaderReflectionApi reflection)
        {
            ArgumentNullException.ThrowIfNull(liveness);
            _native = native;
            _liveness = liveness;
            _cache = cache;
            _flags = flags;
            _diagnostics = diagnostics;
            _reflection = reflection;
        }

        /// <summary>The FXC flags every shader this compiler builds is compiled with.</summary>
        internal uint Flags => _flags;

        /// <summary>The disk cache in use, or null when it is off or the platform reports no cache location.
        /// </summary>
        internal D3D11DxbcCache? Cache => _cache;

        /// <summary>Compile a GLSL 450 vertex and fragment pair into a bound shader set.</summary>
        /// <exception cref="ShaderValidationException">A source failed to compile, FXC rejected the emitted HLSL,
        /// or the vertex input signature is holed (decision S5).</exception>
        internal ID3D11ShaderSet CreateShaderSet(string vertexGlsl, string fragmentGlsl)
        {
            D3D11CompiledPair compiled = _native.Pair(vertexGlsl, fragmentGlsl, _flags, _cache);
            var vertex = D3D11ShaderEvidence.Capture(vertexGlsl, compiled.VertexDxbc, "vertex", _flags, _diagnostics, _reflection);
            var pixel = D3D11ShaderEvidence.Capture(fragmentGlsl, compiled.FragmentDxbc, "pixel", _flags, _diagnostics, _reflection);
            object vertexShader = _native.CreateShader(compiled.VertexDxbc, D3D11ShaderStage.Vertex);
            object? pixelShader = null;
            try
            {
                pixelShader = _native.CreateShader(compiled.FragmentDxbc, D3D11ShaderStage.Fragment);
                return new D3D11ShaderSet(_liveness, vertexShader, pixelShader, compiled.VertexDxbc, vertex, pixel);
            }
            catch
            {
                (pixelShader as IDisposable)?.Dispose();
                ((IDisposable)vertexShader).Dispose();
                throw;
            }
        }

        /// <summary>Compile a GLSL 450 compute source into a bound compute module.</summary>
        /// <exception cref="ShaderValidationException">The source failed to compile, declares no resolvable
        /// workgroup size, or FXC rejected the emitted HLSL.</exception>
        internal IGpuComputeShader CreateComputeShader(string computeGlsl)
        {
            D3D11CompiledCompute compiled = _native.Compute(computeGlsl, _flags, _cache);
            var evidence = D3D11ShaderEvidence.Capture(computeGlsl, compiled.Dxbc, "compute", _flags, _diagnostics, _reflection);
            return new D3D11ComputeShader(_liveness, _native.CreateShader(compiled.Dxbc, D3D11ShaderStage.Compute),
                compiled.Dxbc, compiled.ThreadGroupSizeX, compiled.ThreadGroupSizeY, compiled.ThreadGroupSizeZ, evidence);
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
