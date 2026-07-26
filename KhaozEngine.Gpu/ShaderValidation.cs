using System;
using Veldrid;
using Veldrid.SPIRV;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Device-free validation of a GLSL 450 vertex/fragment shader pair (<see cref="ValidatePair"/>) or a single
    /// compute shader (<see cref="ValidateCompute"/>). Compiles the sources to SPIR-V and cross-compiles them to
    /// every backend shading language the engine targets (HLSL, MSL, GLSL, ESSL), entirely on the CPU via
    /// Veldrid.SPIRV. No <c>GraphicsDevice</c> is created, so this runs in a fast, GPU-free test loop and on CI
    /// machines without a graphics device.
    /// </summary>
    /// <remarks>
    /// This is what the engine's own shader-source tests use to catch a syntax error or a backend miscompile at
    /// build time instead of at first run on a real device of that backend. Games can validate their own custom
    /// shaders the same way in their fast test suites: hand each vertex/fragment pair to
    /// <see cref="ValidatePair(string, string, string?)"/>, or each compute source to
    /// <see cref="ValidateCompute(string, string?)"/>, from a plain unit test.
    /// </remarks>
    public static class ShaderValidation
    {
        // The backend shading languages the engine cross-compiles to at load (matches CreateShadersFromSpirv's reach).
        static readonly CrossCompileTarget[] Targets =
        {
            CrossCompileTarget.HLSL,   // Direct3D 11
            CrossCompileTarget.MSL,    // Metal
            CrossCompileTarget.GLSL,   // OpenGL
            CrossCompileTarget.ESSL,   // OpenGL ES
        };

        /// <summary>
        /// Validates a GLSL 450 vertex + fragment shader pair without a graphics device. First compiles each source
        /// to SPIR-V (entry point <c>main</c>, the same convention as the runtime SPIR-V path
        /// <c>CreateShadersFromSpirv</c>), then cross-compiles the pair to HLSL, MSL, GLSL, and ESSL in turn. A
        /// compile error at any stage throws, so calling this from a test is enough to prove the pair builds on
        /// every backend.
        /// </summary>
        /// <param name="vertexGlsl">The vertex shader source, GLSL <c>#version 450</c>.</param>
        /// <param name="fragmentGlsl">The fragment shader source, GLSL <c>#version 450</c>.</param>
        /// <param name="label">Optional name for the pair, included in any error message so a failure points at the
        /// offending shader.</param>
        /// <exception cref="ShaderValidationException">A source failed to compile to SPIR-V, or the pair failed to
        /// cross-compile to one of the backend targets. The message names the label and the failing stage/target.</exception>
        public static void ValidatePair(string vertexGlsl, string fragmentGlsl, string? label = null)
        {
            if (vertexGlsl is null) throw new ArgumentNullException(nameof(vertexGlsl));
            if (fragmentGlsl is null) throw new ArgumentNullException(nameof(fragmentGlsl));

            string tag = label ?? "shader pair";

            byte[] vertSpirv = CompileToSpirv(vertexGlsl, ShaderStages.Vertex, tag);
            byte[] fragSpirv = CompileToSpirv(fragmentGlsl, ShaderStages.Fragment, tag);

            foreach (CrossCompileTarget target in Targets)
            {
                try
                {
                    SpirvCompilation.CompileVertexFragment(vertSpirv, fragSpirv, target);
                }
                catch (Exception ex)
                {
                    throw new ShaderValidationException(
                        $"{tag}: cross-compile to {target} failed: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Validates a GLSL 450 COMPUTE shader without a graphics device: compiles the source to SPIR-V (entry
        /// point <c>main</c>, the same convention as the runtime path
        /// <see cref="IGpuResourceFactory.CreateComputeShaderFromSpirv"/>), then cross-compiles the single stage to
        /// HLSL, MSL, GLSL, and ESSL in turn. The compute sibling of
        /// <see cref="ValidatePair(string, string, string?)"/>, and the same reason to use it: a compute shader that
        /// miscompiles on one backend otherwise only blows up at first dispatch on a real device of that backend,
        /// whereas this runs in the fast GPU-free test lane on every push.
        /// </summary>
        /// <param name="computeGlsl">The compute shader source, GLSL <c>#version 450</c>, with a
        /// <c>layout(local_size_x = ...) in;</c> workgroup declaration.</param>
        /// <param name="label">Optional name for the shader, included in any error message.</param>
        /// <exception cref="ShaderValidationException">The source failed to compile to SPIR-V, or failed to
        /// cross-compile to one of the backend targets. The message names the label and the failing stage/target.</exception>
        public static void ValidateCompute(string computeGlsl, string? label = null)
        {
            if (computeGlsl is null) throw new ArgumentNullException(nameof(computeGlsl));

            string tag = label ?? "compute shader";
            byte[] spirv = CompileToSpirv(computeGlsl, ShaderStages.Compute, tag);

            foreach (CrossCompileTarget target in Targets)
            {
                try
                {
                    SpirvCompilation.CompileCompute(spirv, target);
                }
                catch (Exception ex)
                {
                    throw new ShaderValidationException(
                        $"{tag}: compute cross-compile to {target} failed: {ex.Message}", ex);
                }
            }
        }

        static byte[] CompileToSpirv(string glsl, ShaderStages stage, string tag)
        {
            try
            {
                return SpirvCompilation.CompileGlslToSpirv(
                    glsl, $"{tag}.{stage}", stage, GlslCompileOptions.Default).SpirvBytes;
            }
            catch (Exception ex)
            {
                throw new ShaderValidationException(
                    $"{tag}: {stage} GLSL -> SPIR-V compile failed: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Thrown by <see cref="ShaderValidation.ValidatePair(string, string, string?)"/> or
    /// <see cref="ShaderValidation.ValidateCompute(string, string?)"/> when a shader source fails to compile to
    /// SPIR-V or fails to cross-compile to a backend target. The message names the shader label and the failing
    /// stage or target.
    /// </summary>
    public sealed class ShaderValidationException : Exception
    {
        /// <summary>Creates the exception with the given message.</summary>
        public ShaderValidationException(string message) : base(message) { }

        /// <summary>Creates the exception with the given message and the underlying compile failure.</summary>
        public ShaderValidationException(string message, Exception inner) : base(message, inner) { }
    }
}
