using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Diagnostics;
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
        readonly D3D11DeviceLiveness _liveness;
        readonly D3D11DxbcCache? _cache;
        readonly uint _flags;

        /// <summary>Builds the compiler for a device, reading the compile flags and the cache location off the
        /// live environment and reporting what it found.</summary>
        internal D3D11ShaderCompiler(ID3D11Device device, D3D11DeviceLiveness liveness)
            : this(device, liveness, D3D11DxbcCache.FromEnvironment(), ResolveFlags())
        {
        }

        /// <summary>The explicit form, for a test that wants a known cache and known flags rather than whatever
        /// the machine's environment says.</summary>
        internal D3D11ShaderCompiler(ID3D11Device device, D3D11DeviceLiveness liveness, D3D11DxbcCache? cache,
            uint flags)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(liveness);

            _device = device;
            _liveness = liveness;
            _cache = cache;
            _flags = flags;
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
            D3D11CompiledPair compiled = D3D11ShaderBuild.Pair(vertexGlsl, fragmentGlsl, _flags, _cache);
            return CreateShaderSetWindows(_device, _liveness, compiled);
        }

        /// <summary>Compile a GLSL 450 compute source into a bound compute module.</summary>
        /// <exception cref="ShaderValidationException">The source failed to compile, declares no resolvable
        /// workgroup size, or FXC rejected the emitted HLSL.</exception>
        internal IGpuComputeShader CreateComputeShader(string computeGlsl)
        {
            D3D11CompiledCompute compiled = D3D11ShaderBuild.Compute(computeGlsl, _flags, _cache);
            return CreateComputeShaderWindows(_device, _liveness, compiled);
        }

        // The two Vortice-touching bodies, behind the package's usual boundary: NoInlining with no interop type
        // in the signature, so nothing resolves the assembly until one actually runs, which is inside a compiler
        // that already holds a live device.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static ID3D11ShaderSet CreateShaderSetWindows(ID3D11Device device, D3D11DeviceLiveness liveness,
            in D3D11CompiledPair compiled)
            => new D3D11ShaderSet(
                liveness,
                device.CreateVertexShader(compiled.VertexDxbc),
                device.CreatePixelShader(compiled.FragmentDxbc),
                compiled.VertexDxbc);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static IGpuComputeShader CreateComputeShaderWindows(ID3D11Device device, D3D11DeviceLiveness liveness,
            in D3D11CompiledCompute compiled)
            => new D3D11ComputeShader(
                liveness,
                device.CreateComputeShader(compiled.Dxbc),
                compiled.Dxbc,
                compiled.ThreadGroupSizeX, compiled.ThreadGroupSizeY, compiled.ThreadGroupSizeZ);

        static uint ResolveFlags()
        {
            uint flags = D3D11ShaderDebug.FromEnvironment(out string? unrecognized);
            if (unrecognized is not null) log.Warn(D3D11ShaderDebug.UnrecognizedWarning(unrecognized));
            else if (flags != D3D11ShaderDebug.Optimized) log.Info(D3D11ShaderDebug.DebugDescription);
            return flags;
        }
    }
}
