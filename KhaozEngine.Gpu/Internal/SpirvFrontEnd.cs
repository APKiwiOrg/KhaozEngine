using System;
using System.Text;
using Silk.NET.Shaderc;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// THE FRONT END: GLSL 450 in, SPIR-V out, glslang and nothing else. Decision V-S3, section 12.3 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>.
    ///
    /// <para>
    /// WHY IT IS ITS OWN FILE. <see cref="SpirvCrossCompile"/> used to carry both halves of the shader toolchain:
    /// this one, and the SPIRV-Cross BACK END that turns SPIR-V into HLSL. Those halves have different consumers
    /// and different futures. The native Direct3D 11 backend needs both. The native Vulkan backend needs ONLY this
    /// one, because Vulkan consumes SPIR-V and <c>vkCreateShaderModule</c> takes the bytes verbatim, so there is no
    /// cross-compilation anywhere on its shader path. Metal, when it arrives, changes the BACK end to add an MSL
    /// target and leaves this file alone.
    /// </para>
    /// <para>
    /// SO THE SPLIT IS WHAT MADE THE SPIRV-CROSS REPLACEMENT (#462, taken in 18.0.0) A CHANGE TO ONE HALF OF ONE FILE
    /// with one consumer family, evaluated against that backend's own goldens rather than against Direct3D 11's
    /// committed ones. It is the entire Metal-facing carrying cost of the Vulkan phase, and it is one file move.
    /// An architecture test asserts that <c>KhaozEngine.Gpu.Vulkan</c> names this type and never names a member of
    /// the back end, so the dependency is a fact about the built IL rather than a convention. The swap proved the
    /// claim: the back end's two emitters were rewritten onto SPIRV-Cross and this file changed only its own
    /// compiler call.
    /// </para>
    /// <para>
    /// THE SIGNATURE NAMES NO TOOLCHAIN TYPE, for the reason every member the native backends reach across
    /// <c>InternalsVisibleTo</c> is: a third-party type in a parameter or a return shape would put that
    /// assembly's reference into a backend's IL through an internal API, and internal API is exactly what a
    /// public-surface scan does not check.
    /// </para>
    /// <para>
    /// THE OPTIONS ARE PINNED IN <see cref="SpirvFrontEndPin"/>, INCLUDING THE OPTIMISATION LEVEL SINCE 18.0.0,
    /// and there is no debug or optimisation knob on this leg anywhere in the repo. That is worth stating because
    /// the Direct3D 11 path has one on ITS leg (<c>KE_D3D11_DEBUG</c>, which reaches FXC and never reaches here)
    /// and a reader will go looking for the equivalent.
    /// </para>
    /// </summary>
    internal static class SpirvFrontEnd
    {
        /// <summary>
        /// THE ONE shaderc API HANDLE, and it is a process-wide singleton because the binding's own
        /// <c>GetApi</c> loads the native library. <c>Shaderc</c> itself is documented thread-safe apart from the
        /// compiler and options objects, which is why those are created per call below rather than shared.
        /// PRIVATE, like every other toolchain-typed member here: the rest of this class is part of the
        /// toolchain-free contract the native backends consume across <c>InternalsVisibleTo</c>.
        /// </summary>
        static readonly Shaderc _shaderc = Shaderc.GetApi();

        /// <summary>
        /// Compile one GLSL 450 source to SPIR-V, entry point <c>main</c>, under the options
        /// <see cref="SpirvFrontEndPin"/> states.
        /// </summary>
        /// <param name="glsl">The shader source, GLSL <c>#version 450</c>.</param>
        /// <param name="stage">Which stage the source is. Exactly one stage flag.</param>
        /// <param name="label">Optional name for the shader, included in any error message.</param>
        /// <exception cref="ShaderValidationException">The source failed to compile to SPIR-V. The message names
        /// the label and the stage.</exception>
        internal static byte[] ToSpirv(string glsl, GpuShaderStages stage, string? label = null)
        {
            if (glsl is null) throw new ArgumentNullException(nameof(glsl));

            string tag = label ?? "shader";
            ShaderKind kind = ToToolchainStage(stage);
            try
            {
                // THE FILE NAME IS A DIAGNOSTIC TAG AND NOT AN INPUT TO THE EMISSION. shaderc uses it to identify
                // the source string in its own error text, and it reaches the module only when debug info is
                // generated, which SpirvFrontEndPin.Debug turns off. The one-off parity measurement recorded in
                // section 12.1 of the Vulkan design is what established that rather than assuming it: the
                // incumbent's own path passed NO file name, and every shipped program still compiled to
                // byte-identical SPIR-V under both. VulkanSpirvIncumbentParityTests re-checked it on every leg
                // until it went with the incumbent in 18.0.0, so a flip of the Debug pin now surfaces as moved
                // bytes in the drift table (VulkanSpirvByteEqualityTests) rather than as a parity failure.
                //
                // THE TAG IS STILL IN THE MEMO'S KEY (#640), precisely because that equality is a fact about the
                // Debug pin rather than a property of the compiler. Keying without it would be correct only while
                // the pin stays false, and would start handing one label's module to another the moment it was
                // flipped, which is the run where a reader most needs the names to be right. Every call site names
                // one source, so carrying it costs no entries.
                return SpirvCompileCache.Shared.GetOrCompile(
                    SpirvFrontEndPin.Identity + ";label=" + tag, stage, glsl,
                    () => Compile(glsl, kind, $"{tag}.{stage}"));
            }
            catch (Exception ex)
            {
                throw new ShaderValidationException($"{tag}: {stage} GLSL to SPIR-V failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// The raw shaderc call, and the ONE place the pinned option set is installed. Every handle it creates is
        /// released on the way out, including on the failure path: a shaderc compiler, its options and its result
        /// are native allocations, and a compile that throws is exactly when a leak is easiest to write and
        /// hardest to see.
        /// <para>
        /// THE OPTIMISATION LEVEL IS PASSED EXPLICITLY, which is the one behavioural change of the toolchain swap
        /// and the reason <see cref="SpirvFrontEndPin.Optimization"/> exists. The outgoing toolchain optimised at
        /// <c>performance</c> and said so nowhere: section 2.3 result 3 of
        /// <c>docs/design/VELDRID-REMOVAL-DESIGN-2026-08-22.md</c> measured three of four sampled sources
        /// compiling to byte-identical LENGTHS at that level and none at <c>zero</c>. Leaving it to a default
        /// would have moved every shipped module for a reason no pin recorded.
        /// </para>
        /// </summary>
        static unsafe byte[] Compile(string glsl, ShaderKind kind, string fileName)
        {
            Compiler* compiler = _shaderc.CompilerInitialize();
            if (compiler is null) throw new InvalidOperationException("shaderc_compiler_initialize returned null.");

            CompileOptions* options = null;
            CompilationResult* result = null;
            try
            {
                options = _shaderc.CompileOptionsInitialize();
                if (options is null)
                    throw new InvalidOperationException("shaderc_compile_options_initialize returned null.");

                _shaderc.CompileOptionsSetTargetEnv(options, SpirvFrontEndPin.TargetEnvironment,
                    SpirvFrontEndPin.TargetEnvironmentVersion);
                _shaderc.CompileOptionsSetTargetSpirv(options, SpirvFrontEndPin.SpirvTarget);
                _shaderc.CompileOptionsSetOptimizationLevel(options, SpirvFrontEndPin.Optimization);
                if (SpirvFrontEndPin.Debug) _shaderc.CompileOptionsSetGenerateDebugInfo(options);

                byte[] source = Encoding.UTF8.GetBytes(glsl);
                fixed (byte* text = source)
                {
                    result = _shaderc.CompileIntoSpv(compiler, text, (nuint)source.Length, kind, fileName,
                        SpirvFrontEndPin.EntryPoint, options);
                }
                if (result is null) throw new InvalidOperationException("shaderc_compile_into_spv returned null.");

                CompilationStatus status = _shaderc.ResultGetCompilationStatus(result);
                if (status != CompilationStatus.Success)
                    throw new InvalidOperationException(
                        status + ": " + (_shaderc.ResultGetErrorMessageS(result) ?? "no message").TrimEnd());

                // The result owns its bytes and frees them with itself, so the module is COPIED out rather than
                // wrapped. A span over native memory that outlives its owner is the one failure mode this shape
                // makes impossible.
                nuint length = _shaderc.ResultGetLength(result);
                var spirv = new byte[(int)length];
                new ReadOnlySpan<byte>(_shaderc.ResultGetBytes(result), spirv.Length).CopyTo(spirv);
                return spirv;
            }
            finally
            {
                if (result is not null) _shaderc.ResultRelease(result);
                if (options is not null) _shaderc.CompileOptionsRelease(options);
                _shaderc.CompilerRelease(compiler);
            }
        }

        // The engine stage flags as shaderc names them, and the ONE outward map from an engine type onto a
        // toolchain type left in this file. Private for the reason the class doc gives: a toolchain type in any
        // non-private signature here would put a toolchain assembly reference into a backend's IL across
        // InternalsVisibleTo, and every backend's premise is declaring none.
        //
        // IT IS A SINGLE-VALUE MAP RATHER THAN A FLAGS FOLD, which is a real difference from the outgoing
        // toolchain and not a simplification. Veldrid's ShaderStages was a [Flags] enum, so the old map ORed
        // several bits together and would silently hand a two-stage value to a compiler that can only compile
        // one. shaderc's ShaderKind is a plain enum with one kind per compile, which is what the member's own
        // contract has always said ("exactly one stage flag"), so a caller that passes two now gets a named
        // refusal instead of whichever stage the fold happened to leave last.
        static ShaderKind ToToolchainStage(GpuShaderStages s) => s switch
        {
            GpuShaderStages.Vertex => ShaderKind.VertexShader,
            GpuShaderStages.Geometry => ShaderKind.GeometryShader,
            GpuShaderStages.TessellationControl => ShaderKind.TessControlShader,
            GpuShaderStages.TessellationEvaluation => ShaderKind.TessEvaluationShader,
            GpuShaderStages.Fragment => ShaderKind.FragmentShader,
            GpuShaderStages.Compute => ShaderKind.ComputeShader,
            _ => throw new ArgumentOutOfRangeException(nameof(s), s,
                "A GLSL to SPIR-V compile is one source at one stage, so this takes exactly one stage flag. "
                + "None and a combination are both refused here rather than compiled as whichever stage came "
                + "last."),
        };
    }
}
