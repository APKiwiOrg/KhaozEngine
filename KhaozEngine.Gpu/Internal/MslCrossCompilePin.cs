using System.Globalization;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// DECISION M-S3, AND THE ONE PLACE THE MSL CROSS-COMPILE OPTIONS ARE WRITTEN DOWN (section 12.3 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>). Every MSL emission the native Metal backend
    /// consumes runs under exactly these values, so a drift is a failing hash rather than a golden nobody can
    /// explain. Until 18.0.0 they were also what kept this path and the incumbent Veldrid Metal one
    /// cross-compiling the same GLSL to the same bytes.
    ///
    /// <para>
    /// THE VALUES ARE THE LIBRARY DEFAULTS, STATED RATHER THAN INHERITED, and the reason is the same one
    /// <see cref="HlslCrossCompilePin"/> records from the other target. <c>VeldridGpuDevice</c>, deleted in
    /// 18.0.0, reached the incumbent's shader path through the three-argument <c>CreateFromSpirv</c>, which
    /// constructs <c>new CrossCompileOptions()</c> and forwards it verbatim, so the incumbent's whole shipped
    /// Metal shader set was cross-compiled under the defaults. If this pin had stated anything else, the parity
    /// measurement would have failed and THIS is what would have had to move. That parity was the entire licence
    /// for reusing the committed <c>metal</c> goldens without a rebake, so the pin is load-bearing rather than
    /// tidy.
    /// </para>
    /// <para>
    /// WHY EACH VALUE IS WHAT IT IS. <see cref="FixClipSpaceZ"/> and <see cref="InvertVertexOutputY"/> append a
    /// clip-space fixup to the emitted vertex shader, and the engine already handles both conventions itself
    /// through <see cref="GpuCapabilities"/>, so a fixup here would apply the correction twice. On Metal that
    /// would be immediately visible, because <c>ClipSpaceYInverted</c> is false and <c>GpuClip.Correct</c> is the
    /// identity. <see cref="NormalizeResourceNames"/> stays off, and section 2.2a measured what flipping it would
    /// buy: it names all 141 layout elements and joins 107 of 159 emitted arguments, which is two thirds of a
    /// mechanism, and it breaks the byte-equality claim in the same move.
    /// <see cref="SpecializationConstantCount"/> is zero because the seam exposes none.
    /// </para>
    /// <para>
    /// <b>WHAT THIS PIN DOES NOT FREEZE, which its name invites the opposite belief about (2.2b, pin 7).</b> It
    /// freezes the <c>CrossCompileOptions</c> the emission is REQUESTED under. It does NOT freeze SPIRV-Cross's
    /// resource NAMING or its index NUMBERING, and no option in this set reaches either. The binding table this
    /// backend builds is read off the emitted argument names, which spell SPIR-V ids as <c>_70</c>, and nothing in
    /// this file promises that convention. What actually freezes the emission is the exact <c>Veldrid.SPIRV</c>
    /// version pinned in <c>Directory.Packages.props</c>, whose bundled <c>libveldrid-spirv</c> carries the
    /// SPIRV-Cross the engine emits through. So the drift this backend is exposed to arrives on a deliberate
    /// package bump rather than on a runner image or an OS update, and <c>MetalShaderIndexTableTests</c> is what
    /// turns that bump into a red device-free leg instead of a wrong pixel. <c>MetalMslIncumbentParityTests</c>
    /// stood beside it until it went away with the incumbent. <c>MslCompilePin</c>, in the Metal backend, carries
    /// the same sentence about the other half of the toolchain. Both shader-cache keys hash that package version
    /// too, read off the loaded assembly by <see cref="SpirvToolchainVersion"/>, so a bump partitions the caches
    /// rather than leaving them answering with the previous cross-compiler's output
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/610">#610</see>).
    /// </para>
    /// <para>
    /// Veldrid-free on purpose, like everything else the native backend reads across
    /// <c>InternalsVisibleTo</c>: <c>CrossCompileOptions</c> is a Veldrid type, so it stays private inside
    /// <see cref="SpirvCrossCompile"/> and is BUILT from these constants rather than being the source of them.
    /// </para>
    /// </summary>
    internal static class MslCrossCompilePin
    {
        /// <summary>No clip-space Z fixup in the emitted vertex shader. The engine reads the device's real depth
        /// range off <see cref="GpuCapabilities"/> and builds its projections for it.</summary>
        internal const bool FixClipSpaceZ = false;

        /// <summary>No clip-space Y inversion in the emitted vertex shader. Same reason: the engine already knows
        /// which way the target's clip space points, and on Metal it points the same way it does in the
        /// source.</summary>
        internal const bool InvertVertexOutputY = false;

        /// <summary>Resource names are left as the module declares them, which for an engine emission with debug
        /// info stripped means SPIRV-Cross invents <c>_&lt;id&gt;</c>. That is not an accident this pin tolerates,
        /// it is the key the binding table joins on (2.2b).</summary>
        internal const bool NormalizeResourceNames = false;

        /// <summary>How many specialization constants are substituted. Zero: the seam exposes none.</summary>
        internal const int SpecializationConstantCount = 0;

        /// <summary>
        /// A stable one-line rendering of the pinned set, for a cache key. Any change to a value above MUST change
        /// this string, because it is what makes a cached emission belong to the options it was emitted under: a
        /// cache hit keyed without it would hand back MSL from the old set forever.
        /// <para>
        /// BUILT FROM THE VALUES, not typed out beside them, which is the whole reason that MUST holds. A
        /// hand-maintained literal is one careless pin change away from a cache bug with no failing test in front
        /// of it. Deriving it moves every cache key by construction instead of by remembering, which is the shape
        /// <see cref="HlslCrossCompilePin.Identity"/> and <see cref="SpirvFrontEndPin.Identity"/> already use.
        /// </para>
        /// </summary>
        internal static readonly string Identity =
            "spirv-cross/msl"
            + ";fixClipSpaceZ=" + Bit(FixClipSpaceZ)
            + ";invertVertexOutputY=" + Bit(InvertVertexOutputY)
            + ";normalizeResourceNames=" + Bit(NormalizeResourceNames)
            + ";specializations=" + SpecializationConstantCount.ToString(CultureInfo.InvariantCulture);

        // 1 / 0 rather than true / false, matching the sibling pins: nothing but a hash reads this token.
        static string Bit(bool value) => value ? "1" : "0";
    }
}
