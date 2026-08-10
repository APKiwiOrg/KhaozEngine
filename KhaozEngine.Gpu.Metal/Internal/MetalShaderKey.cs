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
    /// WHAT IT IS FOR TODAY IS NAMING, WHICH IS NOT NOTHING. The seam's <c>CreateShadersFromSpirv</c> takes two
    /// GLSL strings and no label, so <see cref="ShortTag"/> is the only stable identity a failure message can
    /// print, and every one of the shader path's seven refusal classes prints it (five in the binding table's
    /// join, two in the argument parse in front of it). The <c>.metallib</c> cache this
    /// key was also designed for (M-S7) is REFUSED for v1 with a measurement behind it: section 12.5's in-place
    /// addendum records that no public API can serialize a source-compiled <c>MTLLibrary</c> at all, and that
    /// macOS already caches the MSL-to-library compile across processes at 0.02 ms against 68 to 98 ms cold, both
    /// taken with the compiler service warmed first so neither number is startup cost. The
    /// cache that IS worth building caches the EMISSION instead, and is
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/592">#592</see>. This key is kept whole rather
    /// than trimmed to what naming needs, because that follow-up should not have to re-derive which inputs
    /// matter.
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
        /// differently.</summary>
        internal const string Schema = "khaozengine-metal-program-v1";

        /// <summary>
        /// The engine version, as <c>major.minor.patch</c>, read off this assembly, which the shared
        /// <c>&lt;KhaozEngineVersion&gt;</c> line versions, so nothing is kept in sync by hand.
        /// </summary>
        internal static string EngineVersion { get; } =
            typeof(KhaozEngineMetal).Assembly.GetName().Version?.ToString(3) ?? "unknown";

        /// <summary>
        /// The key for one program. <paramref name="programSources"/> is EVERY GLSL source of the program in its
        /// declared order: both stages of a graphics pair, the single source of a compute kernel.
        /// </summary>
        /// <returns>Lowercase hex SHA-256, 64 characters, safe as a file name on every platform.</returns>
        internal static string For(params string[] programSources)
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
