using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE IDENTITY OF ONE COMPILED METAL PROGRAM: a SHA-256 over everything that can change what the device ends
    /// up executing. The GLSL sources of the WHOLE program, all three pinned option sets of the toolchain, and the
    /// engine version.
    ///
    /// <para>
    /// THE WHOLE PROGRAM'S SOURCES, not just one stage's, and that is the part a per-stage key would get wrong. A
    /// vertex and fragment pair is cross-compiled TOGETHER, because the resource layouts are a property of the
    /// program: SPIRV-Cross assigns indices across the pair at once. So the emitted vertex MSL is a function of
    /// BOTH sources, and a key naming one of them would identify two different emissions with one value. It costs
    /// one extra hashed string to be right.
    /// </para>
    /// <para>
    /// ALL THREE PINS ARE IN THE KEY, for the reason <c>D3D11ShaderKey</c> gives for its two.
    /// <see cref="SpirvFrontEndPin.Identity"/> covers the glslang front end, whose options decide the SPIR-V.
    /// <see cref="MslCrossCompilePin.Identity"/> covers the SPIRV-Cross back end, whose options decide the MSL.
    /// <see cref="MslCompilePin.Identity"/> covers <c>MTLCompileOptions</c>, whose values decide what Metal makes
    /// of that MSL, and <c>fastMathEnabled</c> alone moves every pixel. A key naming two of the three is a silent
    /// time bomb across the change that flips the third.
    /// </para>
    /// <para>
    /// IT NAMES A PROGRAM AND IT KEYS THE CACHE, and it was written for the second before the first was its only
    /// job. The seam's <c>CreateShadersFromSpirv</c> takes two GLSL strings and no label, so
    /// <see cref="ShortTag"/> is the only stable identity a failure message can print, and every one of the
    /// shader path's seven refusal classes prints it (five in the binding table's join, two in the argument parse
    /// in front of it). The <c>.metallib</c> cache this key was originally designed for (M-S7) is REFUSED for v1
    /// with a measurement behind it: section 12.5's in-place addendum records that no public API can serialize a
    /// source-compiled <c>MTLLibrary</c> at all, and that macOS already caches the MSL-to-library compile across
    /// processes at 0.02 ms against 68 to 98 ms cold, both taken with the compiler service warmed first so
    /// neither number is startup cost. What replaced it caches the EMISSION
    /// (<see cref="MetalMslCache"/>, <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/592">#592</see>),
    /// and this key is what it is keyed on. Keeping the key WHOLE at row 9 rather than trimming it to what a
    /// message needs is what let that follow-up be written without re-deriving which inputs matter.
    /// </para>
    /// <para>
    /// THE TWO PRODUCING ASSEMBLIES ARE IN THE KEY BY THEIR MVID, because the payload has FIELDS this key would
    /// otherwise not name. The pins and the sources name the TOOLCHAIN, and four payload fields are produced by
    /// engine code that sits outside it: <see cref="MetalMslEntryPoint"/>'s parse (the entry-point name and every
    /// argument), <see cref="MetalShaderIndexTable"/>'s build on top of <c>SpirvResourceDecorations.Read</c> (the
    /// binding table), <c>SpirvCrossCompile</c>'s reflect (the layouts) and <c>SpirvLocalSize.Parse</c> (a compute
    /// kernel's workgroup size). Within ONE engine version, editing any of those and re-running serves the OLD
    /// payload out of the cache with no error anywhere, which is the wrong-pixel-no-error class arriving through
    /// the cache instead of through a bind. The branch's own planted-entry test is the proof it is reachable: a
    /// hit answers with a stored table for sources the emitter would refuse outright.
    /// </para>
    /// <para>
    /// WHY THE MVID AND NOT A HAND-BUMPED SEMANTICS VERSION. A number in a constant is only correct while every
    /// future editor of four files in two assemblies remembers to move it, and the failure of forgetting is
    /// silent, which is exactly the property that makes this class expensive.
    /// <see cref="System.Reflection.Module.ModuleVersionId"/> is written by the compiler on every build of the
    /// assembly, so it CANNOT be forgotten: any edit to either
    /// assembly, semantic or not, produces a new key. The cost is paid where it is cheap and skipped where it is
    /// not. A developer rebuild re-emits the corpus once, 3.4 seconds measured over the 42 shipped programs, and
    /// that is the CORRECT behaviour rather than an overhead, because a rebuild is precisely when a reader may
    /// have changed. A shipped build's assemblies are built once, so their MVIDs are stable for the life of the
    /// release and a player's cache behaves exactly as before.
    /// </para>
    /// <para>
    /// WHAT THE KEY STILL DOES NOT NAME IS THE CROSS-COMPILER'S OWN VERSION, and a cache reader should know it.
    /// The three pins freeze OPTIONS rather than the emission, and each of their headers says so: what actually
    /// pins the emitted text is the <c>Veldrid.SPIRV</c> package version, which is not in this hash and is not an
    /// engine assembly, so the MVIDs above do not move when it does. Within one engine version a package bump can
    /// therefore leave a cached entry holding the previous cross-compiler's output, on this backend and on
    /// Direct3D 11 alike (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/610">#610</see>). Across
    /// releases the engine version segment covers it, which is the case that matters in the field, and
    /// <c>MetalMslByteEqualityTests</c> deliberately emits fresh so the drift test cannot be answered from a
    /// cache that has it.
    /// </para>
    /// <para>
    /// Pure and device-free, so it is computed and tested identically on every operating system even though only
    /// macOS can turn one into a library.
    /// </para>
    /// </summary>
    internal static class MetalShaderKey
    {
        /// <summary>The key format's own version. Bumped by hand when the FIELDS below change (not their values),
        /// so a reshaped key cannot collide with an old one that happened to hash the same inputs
        /// differently. <c>v2</c> added the two producing assemblies' MVIDs.</summary>
        internal const string Schema = "khaozengine-metal-program-v2";

        /// <summary>
        /// The engine version, as <c>major.minor.patch</c>, read off this assembly, which the shared
        /// <c>&lt;KhaozEngineVersion&gt;</c> line versions, so nothing is kept in sync by hand.
        /// </summary>
        internal static string EngineVersion { get; } =
            typeof(KhaozEngineMetal).Assembly.GetName().Version?.ToString(3) ?? "unknown";

        /// <summary>
        /// The module version id of <c>KhaozEngine.Gpu.Metal</c>, which owns the parse
        /// (<see cref="MetalMslEntryPoint"/>) and the table build (<see cref="MetalShaderIndexTable"/>). The
        /// compiler writes a fresh value into every build of the assembly, so no reader of the emission can add,
        /// change or fix a payload field without moving every key.
        /// </summary>
        internal static Guid MetalModuleId { get; } =
            typeof(MetalMslEntryPoint).Assembly.ManifestModule.ModuleVersionId;

        /// <summary>
        /// The module version id of <c>KhaozEngine.Gpu</c>, which owns the other three payload producers:
        /// <c>SpirvCrossCompile</c>'s reflect (the layouts), <c>SpirvResourceDecorations.Read</c> (the decorations
        /// the table joins on) and <c>SpirvLocalSize.Parse</c> (the workgroup size).
        /// </summary>
        internal static Guid GpuModuleId { get; } =
            typeof(SpirvCrossCompile).Assembly.ManifestModule.ModuleVersionId;

        /// <summary>
        /// The key for one program, under the assemblies this process actually loaded.
        /// <paramref name="programSources"/> is EVERY GLSL source of the program in its declared order: both
        /// stages of a graphics pair, the single source of a compute kernel.
        /// </summary>
        /// <returns>Lowercase hex SHA-256, 64 characters, safe as a file name on every platform.</returns>
        internal static string For(params string[] programSources)
            => For(MetalModuleId, GpuModuleId, programSources);

        /// <summary>
        /// The same key with the two producing assemblies' identities passed in, so the MVID's contribution is
        /// testable without building two engines. Nothing in the shipped path calls this overload.
        /// </summary>
        /// <param name="metalModuleId">Stands in for <see cref="MetalModuleId"/>.</param>
        /// <param name="gpuModuleId">Stands in for <see cref="GpuModuleId"/>.</param>
        /// <param name="programSources">Every GLSL source of the program, in declared order.</param>
        internal static string For(Guid metalModuleId, Guid gpuModuleId, params string[] programSources)
        {
            ArgumentNullException.ThrowIfNull(programSources);
            if (programSources.Length == 0)
            {
                throw new ArgumentException(
                    "A shader key covers a whole program, so it needs at least one source. A key over no sources "
                    + "would be the same value for every shader in the engine.", nameof(programSources));
            }

            var text = new StringBuilder(4096);
            text.Append(Schema).Append('\n')
                .Append(EngineVersion).Append('\n')
                .Append(metalModuleId.ToString("N", CultureInfo.InvariantCulture)).Append('\n')
                .Append(gpuModuleId.ToString("N", CultureInfo.InvariantCulture)).Append('\n')
                .Append(SpirvFrontEndPin.Identity).Append('\n')
                .Append(MslCrossCompilePin.Identity).Append('\n')
                .Append(MslCompilePin.Identity).Append('\n')
                .Append(programSources.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');

            // Each source is LENGTH-PREFIXED, so two sources cannot be confused for one longer one by moving the
            // boundary between them. A plain separator would be forgeable by a source containing the separator.
            foreach (string source in programSources)
            {
                ArgumentNullException.ThrowIfNull(source, nameof(programSources));
                text.Append(source.Length.ToString(CultureInfo.InvariantCulture)).Append('\n').Append(source);
            }

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }

        /// <summary>A short human-readable tag for <paramref name="key"/>, for a message that has to name a
        /// program the caller never labelled.</summary>
        internal static string ShortTag(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return key.Length <= 12 ? key : key[..12];
        }
    }
}
