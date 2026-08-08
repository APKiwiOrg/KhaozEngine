using System;
using Veldrid;
using Veldrid.SPIRV;

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
    /// SO THE SPLIT IS WHAT MAKES THE EVENTUAL SPIRV-CROSS REPLACEMENT (#462) A CHANGE TO ONE HALF OF ONE FILE
    /// with one consumer family, evaluated against that backend's own goldens rather than against Direct3D 11's
    /// committed ones. It is the entire Metal-facing carrying cost of the Vulkan phase, and it is one file move.
    /// An architecture test asserts that <c>KhaozEngine.Gpu.Vulkan</c> names this type and never names a member of
    /// the back end, so the dependency is a fact about the built IL rather than a convention.
    /// </para>
    /// <para>
    /// THE SIGNATURE IS VELDRID-FREE, for the reason every member the native backends reach across
    /// <c>InternalsVisibleTo</c> is: a Veldrid type in a parameter or a return shape would put a Veldrid assembly
    /// reference into a backend's IL through an internal API, and internal API is exactly what a public-surface
    /// scan does not check.
    /// </para>
    /// <para>
    /// THE OPTIONS ARE PINNED IN <see cref="SpirvFrontEndPin"/> and there is no debug or optimisation knob on this
    /// leg anywhere in the repo. That is worth stating because the Direct3D 11 path has one on ITS leg
    /// (<c>KE_D3D11_DEBUG</c>, which reaches FXC and never reaches here) and a reader will go looking for the
    /// equivalent.
    /// </para>
    /// </summary>
    internal static class SpirvFrontEnd
    {
        /// <summary>
        /// The compile options every ENGINE-OWNED SPIR-V emission uses, in ONE place, which is every emission
        /// that comes through this type. The incumbent <see cref="VeldridGpuDevice"/> keeps the library's own
        /// defaults deliberately and does not read these, so the equality of the two sets is asserted by
        /// <c>VulkanSpirvIncumbentParityTests</c> rather than held by construction. PRIVATE, because it is the
        /// one member here whose type is a Veldrid type, and the rest of this class is part of the Veldrid-free
        /// contract the native backends consume across <c>InternalsVisibleTo</c>.
        /// <para>
        /// BUILT FROM <see cref="SpirvFrontEndPin"/> rather than written here, exactly as
        /// <see cref="SpirvCrossCompile"/> builds its own set from <see cref="HlslCrossCompilePin"/>. The pin holds
        /// the values as Veldrid-free constants and derives its cache-key identity from them, so flipping one moves
        /// every derived key by construction instead of by remembering.
        /// </para>
        /// </summary>
        static readonly GlslCompileOptions _options =
            new(SpirvFrontEndPin.Debug, Array.Empty<MacroDefinition>());

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
            ShaderStages veldridStage = VeldridMap.ToVeldrid(stage);
            try
            {
                // THE FILE NAME IS A DIAGNOSTIC TAG AND NOT AN INPUT TO THE EMISSION. shaderc uses it to identify
                // the source string in its own error text, and it reaches the module only when debug info is
                // generated, which SpirvFrontEndPin.Debug turns off. The one-off parity measurement recorded in
                // section 12.1 of the Vulkan design is what established that rather than assuming it: the
                // incumbent's own path passes NO file name, and every shipped program still compiled to
                // byte-identical SPIR-V under both. VulkanSpirvIncumbentParityTests re-checks it on every leg,
                // so a flip of the Debug pin fails there rather than quietly moving the bytes.
                return SpirvCompilation.CompileGlslToSpirv(glsl, $"{tag}.{stage}", veldridStage, _options)
                    .SpirvBytes;
            }
            catch (Exception ex)
            {
                throw new ShaderValidationException($"{tag}: {stage} GLSL to SPIR-V failed: {ex.Message}", ex);
            }
        }
    }
}
