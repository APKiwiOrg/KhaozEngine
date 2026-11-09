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
        readonly D3D11CreationGate _creation;

        /// <summary>Builds the compiler for a device, reading the compile flags and the cache location off the
        /// live environment and reporting what it found.</summary>
        [SupportedOSPlatform("windows")]
        internal D3D11ShaderCompiler(ID3D11Device device, DeviceLiveness liveness, D3D11CreationGate creation, D3D11DiagnosticOptions? diagnostics = null)
            : this(device, liveness, D3D11DxbcCache.FromEnvironment(), ResolveFlags(), diagnostics, creation: creation)
        {
        }

        /// <summary>The explicit form, for a test that wants a known cache and known flags rather than whatever
        /// the machine's environment says.</summary>
        [SupportedOSPlatform("windows")]
        internal D3D11ShaderCompiler(ID3D11Device device, DeviceLiveness liveness, D3D11DxbcCache? cache,
            uint flags, D3D11DiagnosticOptions? diagnostics = null, ID3D11ShaderReflectionApi? reflection = null, D3D11CreationGate? creation = null)
            : this(liveness, new D3D11ShaderCompilerApi(device), cache, flags,
                diagnostics ?? new(false, D3D11RecordMode.Deferred, false, false), reflection ?? new D3D11ShaderReflectionApi(), creation)
        { ArgumentNullException.ThrowIfNull(device); }

        internal D3D11ShaderCompiler(DeviceLiveness liveness, ID3D11ShaderCompilerApi native, D3D11DxbcCache? cache,
            uint flags, D3D11DiagnosticOptions diagnostics, ID3D11ShaderReflectionApi reflection, D3D11CreationGate? creation = null)
        {
            ArgumentNullException.ThrowIfNull(liveness);
            _native = native;
            _liveness = liveness;
            _cache = cache;
            _flags = flags;
            _diagnostics = diagnostics;
            _reflection = reflection;
            _creation = creation ?? D3D11CreationGate.For(null);
        }

        /// <summary>The FXC flags every shader this compiler builds is compiled with.</summary>
        internal uint Flags => _flags;

        /// <summary>The disk cache in use, or null when it is off or the platform reports no cache location.
        /// </summary>
        internal D3D11DxbcCache? Cache => _cache;

        /// <summary>Queue a GLSL pair. Its first bytecode, diagnostic or native consumer joins compilation.</summary>
        /// <exception cref="ShaderValidationException">A source failed to compile, FXC rejected the emitted HLSL,
        /// or the vertex input signature is holed (decision S5).</exception>
        internal ID3D11ShaderSet CreateShaderSet(string vertexGlsl, string fragmentGlsl)
        {
            ArgumentNullException.ThrowIfNull(vertexGlsl);
            ArgumentNullException.ThrowIfNull(fragmentGlsl);
            var build = _native.Compilation;
            var cache = _cache;
            uint flags = _flags;
            var options = _diagnostics;
            var reflection = _reflection;
            string? key = cache is null ? null : cache.Directory + "\0"
                + D3D11ShaderKey.For(D3D11ShaderStage.Vertex, flags, vertexGlsl, fragmentGlsl);
            var compilation = D3D11ShaderCompilation<D3D11CompiledPair>.Start(
                () => build.Pair(vertexGlsl, fragmentGlsl, flags, cache), key);
            var prepared = compilation.Select(compiled =>
            {
                return new D3D11PreparedGraphics(compiled,
                    D3D11ShaderEvidence.Capture(vertexGlsl, compiled.VertexDxbc, "vertex", flags, options, reflection),
                    D3D11ShaderEvidence.Capture(fragmentGlsl, compiled.FragmentDxbc, "pixel", flags, options, reflection));
            });
            return new D3D11ShaderSet(_liveness, prepared, CreateNativeGraphics);
        }

        /// <summary>Queue a compute source. Its first bytecode, metadata or native consumer joins it.</summary>
        internal IGpuComputeShader CreateComputeShader(string computeGlsl)
        {
            ArgumentNullException.ThrowIfNull(computeGlsl);
            var build = _native.Compilation;
            var cache = _cache;
            uint flags = _flags;
            var options = _diagnostics;
            var reflection = _reflection;
            string? key = cache is null ? null : cache.Directory + "\0"
                + D3D11ShaderKey.For(D3D11ShaderStage.Compute, flags, computeGlsl);
            var compilation = D3D11ShaderCompilation<D3D11CompiledCompute>.Start(
                () => build.Compute(computeGlsl, flags, cache), key);
            var prepared = compilation.Select(compiled => new D3D11PreparedCompute(compiled,
                D3D11ShaderEvidence.Capture(computeGlsl, compiled.Dxbc, "compute", flags, options, reflection)));
            return new D3D11ComputeShader(_liveness, prepared, CreateNativeCompute);
        }

        D3D11ShaderSet.Native CreateNativeGraphics(D3D11PreparedGraphics program)
        {
            using var scope = _creation.Enter();
            ObjectDisposedException.ThrowIf(_liveness.IsDead, _native);
            object vertex = _native.CreateShader(program.Code.VertexDxbc, D3D11ShaderStage.Vertex);
            try { return new(vertex, _native.CreateShader(program.Code.FragmentDxbc, D3D11ShaderStage.Fragment)); }
            catch
            {
                if (_liveness.IsAlive) ((IDisposable)vertex).Dispose();
                throw;
            }
        }

        object CreateNativeCompute(D3D11PreparedCompute program)
        {
            using var scope = _creation.Enter();
            ObjectDisposedException.ThrowIf(_liveness.IsDead, _native);
            return _native.CreateShader(program.Code.Dxbc, D3D11ShaderStage.Compute);
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
