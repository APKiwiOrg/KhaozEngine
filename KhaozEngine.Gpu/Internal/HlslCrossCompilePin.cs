using System.Globalization;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// DECISION S3, AND THE ONE PLACE THE CROSS-COMPILE OPTIONS ARE WRITTEN DOWN (section 8.2 of
    /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>). Every HLSL emission in the engine runs under
    /// exactly these values, so a drift is a failing hash rather than a golden nobody can explain. Until 18.0.0
    /// they were also what kept the native Direct3D 11 backend and the incumbent Veldrid one cross-compiling the
    /// same GLSL to the same bytes.
    ///
    /// <para>
    /// WHAT THE FORK ACTUALLY DOES, which is not what the design assumed. Section 8.2 was written expecting
    /// <c>Veldrid.SPIRV.ResourceFactoryExtensions.CreateFromSpirv</c> to derive something from
    /// <c>ResourceBindingModel.Improved</c> that a direct <c>SpirvCompilation.CompileVertexFragment</c> call does
    /// not get for free. It does not. Read against <c>Veldrid.SPIRV</c> 1.0.15 (the version
    /// <c>Directory.Packages.props</c> pins, decompiled from
    /// <c>~/.nuget/packages/veldrid.spirv/1.0.15/lib/netstandard2.0/Veldrid.SPIRV.dll</c>), the pair overload is:
    /// </para>
    /// <code>
    /// public static Shader[] CreateFromSpirv(this ResourceFactory factory, ShaderDescription vs,
    ///     ShaderDescription fs, CrossCompileOptions options)
    /// {
    ///     ...
    ///     CrossCompileTarget target = GetCompilationTarget(factory.BackendType);   // Direct3D11 =&gt; HLSL
    ///     VertexFragmentCompilationResult r = SpirvCompilation.CompileVertexFragment(
    ///         vs.ShaderBytes, fs.ShaderBytes, target, options);                    // options passed STRAIGHT THROUGH
    ///     ...
    /// }
    /// </code>
    /// <para>
    /// The options are forwarded verbatim. The overload the engine's incumbent path called
    /// (<c>VeldridGpuDevice.CreateShadersFromSpirv</c>, the three-argument one with no options, deleted in
    /// 18.0.0) constructed <c>new CrossCompileOptions()</c> and forwarded that, so the incumbent's whole shipped
    /// shader set was cross-compiled under the library DEFAULTS. <c>ResourceBindingModel</c> is not a member of
    /// <c>CrossCompileOptions</c> at all: it lives on <c>GraphicsDeviceOptions</c> and
    /// <c>GraphicsPipelineDescription</c>, and in the vendored fork the only backend that read it was Metal
    /// (<c>src/Veldrid/MTL/MTLPipeline.cs</c>, <c>src/Veldrid/MTL/MTLCommandList.cs</c>). It reached neither the
    /// Direct3D 11 backend nor SPIRV-Cross, so it cannot have moved a byte of emitted HLSL.
    /// </para>
    /// <para>
    /// So the pin is the default set, stated rather than inherited. That is still the point of S3: the values are
    /// now a CHOICE with a name, the byte-equality test hashes what they produce, and the next reader who wants to
    /// flip one has to move a hash table and see every program the flip touched.
    /// </para>
    /// <para>
    /// WHY EACH VALUE IS WHAT IT IS. <see cref="FixClipSpaceZ"/> and <see cref="InvertVertexOutputY"/> are the two
    /// that would silently move pixels: they append a clip-space fixup to the emitted vertex shader, and the
    /// engine already handles both conventions itself through <see cref="GpuCapabilities"/>, so a fixup here would
    /// apply the correction twice. <see cref="NormalizeResourceNames"/> only matters for a target where resource
    /// names are meaningful (GLSL), and Direct3D binds by register rather than by name.
    /// <see cref="SpecializationConstantCount"/> is zero because the seam exposes no specialization constants.
    /// </para>
    /// <para>
    /// WHAT IS NOT HERE, BECAUSE IT IS NOT AN OPTION. The register numbering the emitted HLSL carries is
    /// installed per resource by <see cref="HlslRegisterRemap"/> rather than chosen by a flag, and 18.0.0 is
    /// where it had to become explicit: the outgoing library did that re-numbering itself, silently, and
    /// SPIRV-Cross on its own emits the module's raw <c>Binding</c> decoration instead. A pin cannot express it,
    /// so the pin does not pretend to.
    /// </para>
    /// <para>
    /// Veldrid-free on purpose, like everything else the native backend reads across
    /// <c>InternalsVisibleTo</c>: <c>CrossCompileOptions</c> is a Veldrid type, so it stays private inside
    /// <see cref="SpirvCrossCompile"/> and is BUILT from these constants rather than being the source of them.
    /// </para>
    /// </summary>
    internal static class HlslCrossCompilePin
    {
        /// <summary>No clip-space Z fixup in the emitted vertex shader. The engine reads the device's real depth
        /// range off <see cref="GpuCapabilities"/> and builds its projections for it.</summary>
        internal const bool FixClipSpaceZ = false;

        /// <summary>No clip-space Y inversion in the emitted vertex shader. Same reason: the engine already knows
        /// which way the target's clip space points.</summary>
        internal const bool InvertVertexOutputY = false;

        /// <summary>Resource names are left as the module declares them. Direct3D binds by register.</summary>
        internal const bool NormalizeResourceNames = false;

        /// <summary>How many specialization constants are substituted. Zero: the seam exposes none.</summary>
        internal const int SpecializationConstantCount = 0;

        /// <summary>
        /// A stable one-line rendering of the pinned set, for a cache key. Any change to a value above MUST change
        /// this string, because it is what makes a compiled-shader cache entry belong to the options it was
        /// emitted under: a cache hit keyed without it would hand back bytes from the old set forever.
        /// <para>
        /// BUILT FROM THE VALUES, not typed out beside them, which is the whole reason that MUST holds. A
        /// hand-maintained literal is one careless pin change away from a cache bug with no failing test in front
        /// of it: flip a value, forget the string, and every warm entry under the same engine version keeps
        /// serving DXBC emitted under the old options until someone deletes the directory. Deriving it moves every
        /// cache key by construction instead of by remembering.
        /// </para>
        /// <para>
        /// The token SHAPE is exactly the literal this replaced, so no existing cache key moved.
        /// <c>D3D11ShaderPathTests</c> asserts the whole string against that literal, which is what keeps the
        /// derivation honest about it.
        /// </para>
        /// </summary>
        internal static readonly string Identity =
            "spirv-cross/hlsl"
            + ";fixClipSpaceZ=" + Bit(FixClipSpaceZ)
            + ";invertVertexOutputY=" + Bit(InvertVertexOutputY)
            + ";normalizeResourceNames=" + Bit(NormalizeResourceNames)
            + ";specializations=" + SpecializationConstantCount.ToString(CultureInfo.InvariantCulture);

        // 1 / 0 rather than true / false: nothing but a hash reads this token, and the short form is what the
        // shipped keys were already built with.
        static string Bit(bool value) => value ? "1" : "0";
    }
}
