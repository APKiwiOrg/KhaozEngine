using System;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>The DXBC of one compiled graphics program, both stages.</summary>
    /// <param name="VertexDxbc">The vertex module.</param>
    /// <param name="FragmentDxbc">The fragment (pixel) module.</param>
    internal readonly record struct D3D11CompiledPair(byte[] VertexDxbc, byte[] FragmentDxbc);

    /// <summary>The DXBC of one compiled compute kernel, plus the workgroup size read out of its SPIR-V.</summary>
    /// <param name="Dxbc">The compute module.</param>
    /// <param name="ThreadGroupSizeX">Workgroup size on X, from the shader's own declaration.</param>
    /// <param name="ThreadGroupSizeY">Workgroup size on Y.</param>
    /// <param name="ThreadGroupSizeZ">Workgroup size on Z.</param>
    internal readonly record struct D3D11CompiledCompute(
        byte[] Dxbc, uint ThreadGroupSizeX, uint ThreadGroupSizeY, uint ThreadGroupSizeZ);

    /// <summary>
    /// THE SHADER PATH WITHOUT A DEVICE: GLSL 450 in, DXBC out, decision S1 end to end. The single source stays
    /// GLSL, the internal Veldrid-free helpers in <c>KhaozEngine.Gpu</c> do GLSL to SPIR-V
    /// (<see cref="SpirvFrontEnd"/>, under <see cref="SpirvFrontEndPin"/>) and then SPIR-V to HLSL
    /// (<see cref="SpirvCrossCompile"/>, under <see cref="HlslCrossCompilePin"/>, decision S3), and
    /// <see cref="D3D11Fxc"/> makes the FXC call, through the disk cache when there is one (decision S4). The
    /// reflected vertex input signature is checked for the holed-<c>TEXCOORD</c> hazard on the way out (decision
    /// S5). This backend is the one consumer that needs BOTH halves of that toolchain.
    ///
    /// <para>
    /// NO DEVICE IS INVOLVED, which is what makes this type reusable rather than an implementation detail of the
    /// factory. Two callers want exactly this and only this: the resource factory, which turns the bytes into
    /// Direct3D objects, and the validation leg <see cref="D3D11ShaderValidation"/>, which turns them into
    /// nothing and exists purely to make FXC reject bad HLSL in CI. Sharing the path is the point: a validation
    /// leg compiling under different flags, different options or a different profile would be validating a shader
    /// nobody ships.
    /// </para>
    /// <para>
    /// THE CACHE IS CHECKED BEFORE THE CROSS-COMPILE, not after. A program whose two stages are both cached skips
    /// SPIRV-Cross entirely, which is the larger half of the cold-start cost, not just the FXC call. That is also
    /// why the contiguity check reflects the DXBC rather than reading the cross-compile's own reflection: on a
    /// cache hit there IS no cross-compile reflection, and the check has to hold either way.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class D3D11ShaderBuild
    {
        /// <summary>
        /// Compile a vertex and fragment GLSL pair to DXBC. <paramref name="cache"/> may be null to compile
        /// unconditionally, which is what the validation leg does, because a cache hit would mean FXC never ran.
        /// </summary>
        /// <exception cref="ShaderValidationException">A source failed to compile to SPIR-V, the pair failed to
        /// cross-compile, FXC rejected the emitted HLSL, or the vertex input signature is holed.</exception>
        internal static D3D11CompiledPair Pair(string vertexGlsl, string fragmentGlsl, uint flags,
            D3D11DxbcCache? cache, string? label = null)
        {
            ArgumentNullException.ThrowIfNull(vertexGlsl);
            ArgumentNullException.ThrowIfNull(fragmentGlsl);

            string vertexKey = D3D11ShaderKey.For(D3D11ShaderStage.Vertex, flags, vertexGlsl, fragmentGlsl);
            string fragmentKey = D3D11ShaderKey.For(D3D11ShaderStage.Fragment, flags, vertexGlsl, fragmentGlsl);
            string tag = label ?? ("d3d11 program " + D3D11ShaderKey.ShortTag(vertexKey));

            byte[]? vertexDxbc = cache?.TryRead(vertexKey);
            byte[]? fragmentDxbc = cache?.TryRead(fragmentKey);

            if (vertexDxbc is null || fragmentDxbc is null)
            {
                CrossCompiledPair hlsl = SpirvCrossCompile.GlslPairToHlsl(vertexGlsl, fragmentGlsl, tag);
                vertexDxbc ??= CompileAndCache(
                    hlsl.VertexSource, D3D11ShaderStage.Vertex, flags, cache, vertexKey, tag + " [vertex]");
                fragmentDxbc ??= CompileAndCache(
                    hlsl.FragmentSource, D3D11ShaderStage.Fragment, flags, cache, fragmentKey, tag + " [fragment]");
            }

            // DECISION S5. Reflected from the bytes rather than from the cross-compile, so a cached module is
            // checked exactly as a freshly compiled one is.
            D3D11ShaderSignature.RequireContiguousUserSemantics(
                D3D11Fxc.ReflectVertexInputs(vertexDxbc, tag), tag);

            return new D3D11CompiledPair(vertexDxbc, fragmentDxbc);
        }

        /// <summary>
        /// Compile a compute GLSL source to DXBC, reading the workgroup size out of the SPIR-V on the way through.
        /// <paramref name="cache"/> may be null, as for <see cref="Pair"/>.
        /// <para>
        /// THE SPIR-V IS COMPILED HERE EVEN ON A CACHE HIT, because the workgroup size lives in the module and
        /// nothing else reports it: <c>Veldrid.SPIRV</c>'s compute result carries only the cross-compiled source
        /// and a resource-layout reflection, and Direct3D takes the size from the module without ever handing it
        /// back. What stops this cache carrying the numbers anyway is its PAYLOAD: an entry here is a bare DXBC
        /// blob written straight to disk, with no header, no version and nowhere to put three more integers, so
        /// carrying them would mean inventing a container and a format version for this backend.
        /// </para>
        /// <para>
        /// THE METAL SIBLING DOES CARRY THEM, AND THAT IS SOUND RATHER THAN INCONSISTENT.
        /// <c>MetalMslCacheEntry</c> is a written structure with a magic, a format version and an authenticating
        /// hash already, because its payload is MSL plus a binding table and neither has a reader below it that
        /// would refuse a mangled one. Three more integers in a structure that exists cost twelve bytes and buy
        /// the other half of that backend's compile, so a Metal compute hit skips glslang as well. Drift is not
        /// the reason to refuse them on either backend: both keys hash the shader's own source, so a changed
        /// shader is a different entry rather than a stale one, and the numbers cannot disagree with the module
        /// they were read from. The reason here is the format, and a maintainer who gives this cache a container
        /// may carry them too.
        /// </para>
        /// </summary>
        /// <exception cref="ShaderValidationException">The source failed to compile to SPIR-V, failed to
        /// cross-compile, declares no resolvable workgroup size, or FXC rejected the emitted HLSL.</exception>
        internal static D3D11CompiledCompute Compute(string computeGlsl, uint flags, D3D11DxbcCache? cache,
            string? label = null)
        {
            ArgumentNullException.ThrowIfNull(computeGlsl);

            string key = D3D11ShaderKey.For(D3D11ShaderStage.Compute, flags, computeGlsl);
            string tag = label ?? ("d3d11 compute " + D3D11ShaderKey.ShortTag(key));

            byte[] spirv = SpirvFrontEnd.ToSpirv(computeGlsl, GpuShaderStages.Compute, tag);
            (uint x, uint y, uint z) = SpirvLocalSize.Parse(spirv, tag);

            byte[]? dxbc = cache?.TryRead(key);
            if (dxbc is null)
            {
                CrossCompiledCompute hlsl = SpirvCrossCompile.ComputeToHlsl(spirv, tag);
                dxbc = CompileAndCache(hlsl.ComputeSource, D3D11ShaderStage.Compute, flags, cache, key, tag);
            }

            return new D3D11CompiledCompute(dxbc, x, y, z);
        }

        static byte[] CompileAndCache(string hlsl, D3D11ShaderStage stage, uint flags, D3D11DxbcCache? cache,
            string key, string label)
        {
            byte[] dxbc = D3D11Fxc.Compile(hlsl, D3D11ShaderProfile.For(stage), flags, label);
            cache?.TryWrite(key, dxbc);   // best effort by contract: a cache that cannot be written is a slower start
            return dxbc;
        }
    }
}
