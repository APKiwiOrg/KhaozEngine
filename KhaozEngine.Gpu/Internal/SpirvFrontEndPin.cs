using System.Globalization;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// DECISION V-S2, AND THE ONE PLACE THE FRONT-END OPTIONS ARE WRITTEN DOWN (section 12.1 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>). Every ENGINE-OWNED GLSL to SPIR-V compile
    /// runs under exactly these values, which means every call that goes through <see cref="SpirvFrontEnd"/>:
    /// both native backends and <c>ShaderValidation</c>. It is NOT every compile in the process, and the
    /// difference matters. Until 18.0.0 the incumbent <c>VeldridGpuDevice</c> deliberately kept the library's own
    /// defaults on both of its paths, so the two sets were maintained independently and their equality was
    /// ASSERTED by <c>VulkanSpirvIncumbentParityTests</c> rather than guaranteed by construction. Both are gone
    /// with the incumbent, and that assertion is what kept the native Vulkan backend handing
    /// <c>vkCreateShaderModule</c> the same bytes the incumbent handed it, and so what licensed the 36 committed
    /// goldens carrying over as a test of the BACKEND rather than of the compiler.
    ///
    /// <para>
    /// V-S2 IS TWO ARTEFACTS AND THIS IS ONLY THE FIRST. The second was the parity check against the incumbent's
    /// own path, first taken as a ONE-OFF in-process measurement and RECORDED in section 12.1 of the design,
    /// which is what licensed carrying the goldens over without a rebake, and then asserted continuously on every
    /// leg by <c>VulkanSpirvIncumbentParityTests</c> until it went with the incumbent, leaving the recorded
    /// measurement as what survives. <c>VulkanSpirvByteEqualityTests</c> is neither: it is a DRIFT detector baked
    /// from this path's own emission, so a green run there is not parity evidence and reading it as such reads it
    /// backwards. All of it was needed and none of it substituted for the rest.
    /// </para>
    /// <para>
    /// WHAT THE INCUMBENT ACTUALLY DID, read rather than assumed. BOTH of <c>VeldridGpuDevice</c>'s shader paths
    /// called <c>SpirvCompilation.CompileGlslToSpirv</c> themselves under <c>GlslCompileOptions.Default</c> and
    /// handed the resulting module to <c>Veldrid.SPIRV</c>'s <c>CreateFromSpirv</c>.
    /// <c>CreateComputeShaderFromSpirv</c> always did, so it could read the workgroup size back, and
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/640">#640</see> moved
    /// <c>CreateShadersFromSpirv</c> to the same shape so one memo could sit in front of glslang for a wrapper
    /// that was recompiling the same <c>const string</c> on every device. Neither went through this pin, on
    /// purpose, and both left the graph with the incumbent. Handing that library a module rather
    /// than a source changes nothing below it, because every branch of it sniffs the SPIR-V header first.
    /// <c>EnsureSpirv</c> does on the Vulkan branch, where the bytes reach <c>vkCreateShaderModule</c> with no
    /// cross-compilation at all because Vulkan consumes SPIR-V, and
    /// <c>SpirvCompilation.CompileVertexFragment</c> does per stage on every other. The only thing that differed
    /// between the two option sets was the diagnostic FILE NAME, which the incumbent left null on its graphics
    /// path and named <c>compute</c> on its compute one, while this engine sets
    /// <c>&lt;label&gt;.&lt;stage&gt;</c>. The parity check is what established that the name never reaches the
    /// module while <see cref="Debug"/> is false.
    /// </para>
    /// <para>
    /// WHY EACH VALUE IS WHAT IT IS. <see cref="Debug"/> off is the value the engine has always shipped under, and
    /// it is the one that decides whether glslang writes source text and line tables into the module. Turning it on
    /// would grow every module, change every hash and change nothing a player sees, which is why there is no
    /// environment knob for it here and why the Direct3D 11 backend's <c>KE_D3D11_DEBUG</c> is not an analogue: that
    /// gate reaches FXC and never reaches this leg. <see cref="MacroCount"/> is zero because the engine's GLSL
    /// carries no preprocessor variants, and adding one would fork every program's SPIR-V by definition.
    /// <see cref="EntryPoint"/> is <c>main</c>, which is the convention the seam documents and every shipped source
    /// obeys.
    /// </para>
    /// <para>
    /// Veldrid-free on purpose, like <see cref="HlslCrossCompilePin"/>: <c>GlslCompileOptions</c> is a Veldrid type,
    /// so it stays private inside <see cref="SpirvFrontEnd"/> and is BUILT from these constants rather than being
    /// the source of them.
    /// </para>
    /// </summary>
    internal static class SpirvFrontEndPin
    {
        /// <summary>No debug information in the emitted module. Off is what the whole shipped shader set was
        /// compiled under, and flipping it moves every module's bytes.</summary>
        internal const bool Debug = false;

        /// <summary>How many preprocessor macros are defined for the compile. Zero: the engine ships one variant
        /// of every source.</summary>
        internal const int MacroCount = 0;

        /// <summary>The entry point name every stage declares. Not an option to the compiler, but part of the
        /// identity of what was compiled, because a module named at a different entry point is a different
        /// module to <c>vkCreateShaderModule</c>'s consumer.</summary>
        internal const string EntryPoint = "main";

        /// <summary>
        /// A stable one-line rendering of the pinned set, for a cache key. Any change to a value above MUST change
        /// this string, because it is what makes a cached compiled artefact belong to the options it was emitted
        /// under: a cache hit keyed without it would hand back bytes from the old set forever.
        /// <para>
        /// BUILT FROM THE VALUES, not typed out beside them, which is the whole reason that MUST holds. A
        /// hand-maintained literal is one careless pin change away from a cache bug with no failing test in front
        /// of it.
        /// </para>
        /// <para>
        /// Its consumer today is <c>D3D11ShaderKey</c>, which hashes it alongside
        /// <see cref="HlslCrossCompilePin.Identity"/> because a DXBC entry is a function of BOTH halves of the
        /// toolchain. The Vulkan backend's own module dedup needs no identity token at all: it keys on a hash of
        /// the SPIR-V itself, and the bytes already embody every option that produced them.
        /// </para>
        /// </summary>
        internal static readonly string Identity =
            "glslang/spirv"
            + ";debug=" + Bit(Debug)
            + ";macros=" + MacroCount.ToString(CultureInfo.InvariantCulture)
            + ";entryPoint=" + EntryPoint;

        // 1 / 0 rather than true / false, matching the shape HlslCrossCompilePin's token already uses.
        static string Bit(bool value) => value ? "1" : "0";
    }
}
