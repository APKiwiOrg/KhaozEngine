using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE SHADER PATH WITHOUT A DEVICE: GLSL 450 in, MSL plus a binding table out, decision M-S1 end to end. The
    /// single source stays GLSL, <see cref="SpirvFrontEnd"/> compiles it to SPIR-V under
    /// <see cref="SpirvFrontEndPin"/>, <see cref="SpirvCrossCompile"/> emits MSL under
    /// <see cref="MslCrossCompilePin"/>, and the emission is then READ: its entry-point names by
    /// <see cref="MetalMslEntryPoint"/>, its indices by <see cref="MetalShaderIndexTable"/>.
    ///
    /// <para>
    /// EVERY STAGE'S SPIR-V IS HELD UNTIL THE TABLE IS BUILT, and that is the shape 2.2b costed the id join at.
    /// The path already compiles each stage's GLSL to SPIR-V itself and hands the pair to the cross-compiler, so
    /// at the moment the MSL text exists each module is sitting in a local. The decoration walk is one linear pass
    /// over words already in hand. Nothing is compiled twice to get them back, which is also why
    /// <see cref="SpirvCrossCompile"/> has no GLSL-source convenience for the MSL target.
    /// </para>
    /// <para>
    /// NO DEVICE IS INVOLVED, which is what makes this type the seat of the index-table test rather than an
    /// implementation detail of the factory. It runs over every shipped program on the free Linux leg, on every
    /// <c>dotnet test</c>, and it is where "everything compiles and every pixel is wrong" is caught before any
    /// device exists. The device half is one <c>newLibraryWithSource:</c> per stage and a
    /// <c>newFunctionWithName:</c> per entry point, and that is genuinely all of it.
    /// </para>
    /// <para>
    /// AND THE COMPUTE WORKGROUP SIZE COMES OFF THE MODULE, unchanged (<see cref="SpirvLocalSize"/>). Metal needs
    /// the same numbers for <c>dispatchThreadgroups</c>'s <c>threadsPerThreadgroup</c> that the seam's
    /// <c>IGpuComputeShader.ThreadGroupSize*</c> reports, and MSL does not carry the size the way SPIR-V does, so
    /// the module is the only source. The incumbent took them from
    /// <c>ComputePipelineDescription.ThreadGroupSize*</c>, which is that same source one layer up. On a cache hit
    /// it comes out of the payload instead, which is the one place a hit answers something the emission would
    /// have answered (<see cref="MetalMslCacheEntry"/> carries why).
    /// </para>
    /// <para>
    /// THE CACHE IS CONSULTED BEFORE THE FRONT END, not between the front end and the cross-compile, and that is
    /// the whole of what makes it worth having (#592). The engine's half of a cold start is glslang plus
    /// SPIRV-Cross, measured at 4,168 ms over the shipped corpus, and a hit skips BOTH. What comes back is the
    /// emission and the table together, because the table is read out of the emission and a hit that returned
    /// only MSL would have to re-parse it (2.2b, pin 6). Nothing downstream can tell a hit from a miss, which is
    /// the property <c>MetalIndexTableCache</c> depends on: a cached table reaches the per-device dedup through
    /// the same call, so two programs sharing a table still share ONE instance and M-R9's handle compare holds.
    /// </para>
    /// </summary>
    internal static class MetalShaderBuild
    {
        /// <summary>
        /// Emit a vertex and fragment GLSL pair to MSL and read its binding table.
        /// </summary>
        /// <param name="vertexGlsl">The vertex source, GLSL <c>#version 450</c>.</param>
        /// <param name="fragmentGlsl">The fragment source, GLSL <c>#version 450</c>.</param>
        /// <param name="cache">The emission cache, or null to emit unconditionally. Null is what a test that
        /// wants to measure or compare the emission itself passes.</param>
        /// <param name="label">Optional name for the program, included in every error message.</param>
        /// <exception cref="ShaderValidationException">A source failed to compile to SPIR-V, the pair failed to
        /// cross-compile, or the emission could not be read (any of 2.2b pin 1's classes).</exception>
        internal static MetalMslProgram Pair(string vertexGlsl, string fragmentGlsl, MetalMslCache? cache,
            string? label = null)
        {
            ArgumentNullException.ThrowIfNull(vertexGlsl);
            ArgumentNullException.ThrowIfNull(fragmentGlsl);

            string key = MetalShaderKey.For(vertexGlsl, fragmentGlsl);
            string tag = label ?? "metal program " + MetalShaderKey.ShortTag(key);

            if (cache?.TryLoad(key, tag) is { } hit) return hit.Program;

            byte[] vertexSpirv = SpirvFrontEnd.ToSpirv(vertexGlsl, GpuShaderStages.Vertex, tag);
            byte[] fragmentSpirv = SpirvFrontEnd.ToSpirv(fragmentGlsl, GpuShaderStages.Fragment, tag);
            CrossCompiledPair msl = SpirvCrossCompile.VertexFragmentToMsl(vertexSpirv, fragmentSpirv, tag);

            MetalMslProgram program = Assemble(msl.Reflection, tag,
                (MetalShaderStage.Vertex, msl.VertexSource, vertexSpirv),
                (MetalShaderStage.Fragment, msl.FragmentSource, fragmentSpirv));

            // AFTER the assemble rather than beside it, so a program whose emission cannot be READ is never
            // cached: an entry that reproduces a throw on every launch is a slower failure, not a cache.
            cache?.TryStore(key, new MetalMslCacheEntry(program, 0, 0, 0));
            return program;
        }

        /// <summary>
        /// Emit a compute GLSL source to MSL, read its binding table, and read its workgroup size out of the
        /// module.
        /// </summary>
        /// <param name="computeGlsl">The compute source, GLSL <c>#version 450</c>, with a
        /// <c>layout(local_size_x = ...) in;</c> declaration.</param>
        /// <param name="cache">The emission cache, or null to emit unconditionally.</param>
        /// <param name="label">Optional name for the shader, included in every error message.</param>
        /// <exception cref="ShaderValidationException">The source failed to compile to SPIR-V, failed to
        /// cross-compile, declares no resolvable workgroup size, or its emission could not be read.</exception>
        internal static (MetalMslProgram Program, uint X, uint Y, uint Z) Compute(string computeGlsl,
            MetalMslCache? cache, string? label = null)
        {
            ArgumentNullException.ThrowIfNull(computeGlsl);

            string key = MetalShaderKey.For(computeGlsl);
            string tag = label ?? "metal compute " + MetalShaderKey.ShortTag(key);

            if (cache?.TryLoad(key, tag) is { } hit)
                return (hit.Program, hit.ThreadGroupSizeX, hit.ThreadGroupSizeY, hit.ThreadGroupSizeZ);

            byte[] spirv = SpirvFrontEnd.ToSpirv(computeGlsl, GpuShaderStages.Compute, tag);
            (uint x, uint y, uint z) = SpirvLocalSize.Parse(spirv, tag);
            CrossCompiledCompute msl = SpirvCrossCompile.ComputeToMsl(spirv, tag);

            MetalMslProgram program = Assemble(msl.Reflection, tag,
                (MetalShaderStage.Compute, msl.ComputeSource, spirv));

            cache?.TryStore(key, new MetalMslCacheEntry(program, x, y, z));
            return (program, x, y, z);
        }

        // ONE read of the emission feeds both halves: the entry-point name the device asks the library for, and
        // the arguments the table joins. Parsing twice would let the two disagree, which is the shape M-S5 warns
        // about from the other direction (a function looked up by a name nothing read).
        static MetalMslProgram Assemble(ShaderReflection reflection, string tag,
            params (MetalShaderStage Stage, string Msl, byte[] Spirv)[] stages)
        {
            var emitted = new MetalMslStage[stages.Length];
            var joins = new List<MetalMslStageJoin>(stages.Length);

            for (int i = 0; i < stages.Length; i++)
            {
                (MetalShaderStage stage, string msl, byte[] spirv) = stages[i];
                (string name, List<MetalMslArgument> arguments) = MetalMslEntryPoint.Parse(msl, stage, tag);

                emitted[i] = new MetalMslStage(stage, name, msl);
                joins.Add(new MetalMslStageJoin(stage, spirv, arguments));
            }

            return new MetalMslProgram(
                emitted, MetalShaderIndexTable.Build(reflection.ResourceLayouts, joins, tag));
        }
    }
}
