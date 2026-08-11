using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE IDENTITY OF ONE COMPILED SHADER, decision S4's key. A SHA-256 over everything that can change the DXBC
    /// bytes: the GLSL sources of the whole program, the FXC target profile, the FXC flags, BOTH pinned option
    /// sets of the shader toolchain, and the engine version.
    ///
    /// <para>
    /// THE WHOLE PROGRAM'S SOURCES, not just the stage's own, and that is the part a per-stage key would get
    /// wrong. A vertex and fragment pair is cross-compiled TOGETHER, because the resource layouts are a property
    /// of the program: SPIRV-Cross assigns registers across both stages at once, and it drops a vertex input the
    /// vertex stage does not read regardless of what the fragment stage does. So the emitted vertex HLSL is a
    /// function of BOTH sources, and a cache keyed on the vertex source alone would serve stale bytes the moment
    /// a fragment shader changed in a way that renumbered a register. It costs one extra hashed string to be
    /// right.
    /// </para>
    /// <para>
    /// BOTH TOOLCHAIN PINS ARE IN THE KEY for the same reason. <see cref="HlslCrossCompilePin.Identity"/> covers
    /// the SPIRV-Cross back end, whose options decide the emitted HLSL.
    /// <see cref="SpirvFrontEndPin.Identity"/> covers the glslang FRONT end, whose options decide the SPIR-V the
    /// back end reads, so a DXBC entry is a function of both halves and a key naming one of them is a silent time
    /// bomb across the change that flips the other. The front-end half arrived with decision V-S3's split
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/526), which is what made the front-end options a pinned
    /// thing with a name rather than an inline library default nobody could point at.
    /// </para>
    /// <para>
    /// AND THE CROSS-COMPILER'S OWN VERSION IS IN THE KEY, which neither pin covers
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/610">#610</see>). Both pins freeze the OPTIONS
    /// the toolchain is asked for, and each of their headers says so: what freezes the emitted HLSL itself is the
    /// <c>Veldrid.SPIRV</c> package, which carries glslang and SPIRV-Cross and is not an engine assembly, so the
    /// engine version does not move when it does. Within one engine version a package bump therefore used to
    /// leave every cached stage answering with the previous cross-compiler's output, on this backend and on Metal
    /// alike, and the symptom is that nothing changes when it should.
    /// <see cref="SpirvToolchainVersion.Identity"/> is that package read off the assembly this process loaded,
    /// and hashing it is what makes a bump partition the cache instead.
    /// </para>
    /// <para>
    /// THE ENGINE VERSION IS BELT AND BRACES on top of all of that. The five components above should already be
    /// complete, but the cost of being wrong is a stale shader that renders subtly incorrectly on a developer
    /// machine and correctly everywhere else, which is the worst failure this cache can produce. Versioning the
    /// key means an engine upgrade cannot inherit one, whatever else changed. It is also in the cache DIRECTORY
    /// (see <see cref="D3D11DxbcCache"/>), so an old version's entries are a single prunable folder rather than
    /// unreachable files that never expire.
    /// </para>
    /// <para>
    /// Pure, device-free and free of any Direct3D type, so the key is computed and tested identically on every
    /// operating system even though only Windows can turn one into bytes.
    /// </para>
    /// </summary>
    internal static class D3D11ShaderKey
    {
        /// <summary>The key format's own version. Bumped by hand when the FIELDS below change (not their values),
        /// so a reshaped key cannot collide with an old one that happened to hash the same inputs differently.
        /// <c>v3</c> added the cross-compiler's own package version.
        /// </summary>
        internal const string Schema = "khaozengine-d3d11-dxbc-v3";

        /// <summary>
        /// The engine version the key and the cache directory carry, as <c>major.minor.patch</c>. Read off this
        /// assembly, which is versioned by the shared <c>&lt;KhaozEngineVersion&gt;</c> line, so nothing has to be
        /// kept in sync by hand.
        /// </summary>
        internal static string EngineVersion { get; } =
            typeof(KhaozEngineD3D11).Assembly.GetName().Version?.ToString(3) ?? "unknown";

        /// <summary>
        /// The key for one stage of a program. <paramref name="programSources"/> is EVERY GLSL source of the
        /// program in its declared order (both stages of a graphics pair, the single source of a compute kernel),
        /// and <paramref name="stage"/> selects which emitted stage this key is for.
        /// </summary>
        /// <param name="stage">Which stage's DXBC this key identifies.</param>
        /// <param name="flags">The FXC flags the compile will use (see <see cref="D3D11ShaderDebug"/>).</param>
        /// <param name="programSources">Every GLSL source of the program, in declaration order.</param>
        /// <returns>Lowercase hex SHA-256, 64 characters, safe as a file name on every platform.</returns>
        internal static string For(D3D11ShaderStage stage, uint flags, params string[] programSources)
            => For(stage, flags, SpirvToolchainVersion.Identity, programSources);

        /// <summary>
        /// The same key with the cross-compiler's identity passed in, so its contribution is testable without
        /// building against two packages. Nothing in the shipped path calls this overload.
        /// <para>
        /// THE SOURCE ARRAY IS NOT <c>params</c> HERE, deliberately. Two <c>params</c> overloads whose expanded
        /// shapes both end in strings would let a call meant for one bind to the other, folding the identity into
        /// the source list or a source into the identity, and a key is precisely the place where a silent
        /// mis-bind costs stale bytes with no error. The shipped entry point above keeps the convenience.
        /// </para>
        /// </summary>
        /// <param name="stage">Which stage's DXBC this key identifies.</param>
        /// <param name="flags">The FXC flags the compile will use.</param>
        /// <param name="spirvToolchain">Stands in for <see cref="SpirvToolchainVersion.Identity"/>.</param>
        /// <param name="programSources">Every GLSL source of the program, in declaration order.</param>
        internal static string For(D3D11ShaderStage stage, uint flags, string spirvToolchain,
            string[] programSources)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(spirvToolchain);
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
                .Append(spirvToolchain).Append('\n')
                .Append(SpirvFrontEndPin.Identity).Append('\n')
                .Append(HlslCrossCompilePin.Identity).Append('\n')
                .Append(D3D11ShaderProfile.For(stage)).Append('\n')
                .Append(flags.ToString("x8", CultureInfo.InvariantCulture)).Append('\n')
                .Append(D3D11ShaderProfile.EntryPoint).Append('\n')
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

        /// <summary>
        /// A short human-readable tag for <paramref name="key"/>, for a message that has to name a shader the
        /// caller never labelled. The seam's <c>CreateShadersFromSpirv</c> takes two GLSL strings and no name, so
        /// this is the only stable identity a failure can print, and it is the SAME value the cache file is named
        /// after and the byte-equality hash table lists, which is what makes it greppable rather than decorative.
        /// </summary>
        internal static string ShortTag(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return key.Length <= 12 ? key : key.Substring(0, 12);
        }
    }
}
