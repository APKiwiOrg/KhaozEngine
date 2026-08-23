using System;
using System.Reflection;
using Silk.NET.Shaderc;
using Silk.NET.SPIRV.Cross;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// THE VERSION OF THE THING THAT ACTUALLY PRODUCES THE BYTES, as one string a shader-cache key can hash
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/610">#610</see>). Every option pin in this
    /// engine freezes what the toolchain is ASKED for, and each of their headers says so in as many words. What
    /// freezes the emitted text itself is the toolchain package version, and no pin reaches it. Since 18.0.0
    /// that is TWO packages, <c>Silk.NET.Shaderc</c> for the glslang front end and <c>Silk.NET.SPIRV.Cross</c>
    /// for both back ends, where the outgoing <c>Veldrid.SPIRV</c> carried both in one.
    ///
    /// <para>
    /// WHY A KEY NEEDS IT. The engine version partitions the caches across releases, because it is both a key
    /// component and a cache-directory segment, so a released bump can never inherit an entry. Within ONE engine
    /// version it does not: a developer bumping a toolchain package in <c>Directory.Packages.props</c> without
    /// bumping <c>&lt;KhaozEngineVersion&gt;</c>, or moving across a branch that did, leaves every cached program
    /// on both keyed backends answering with the PREVIOUS cross-compiler's output. The symptom is that nothing
    /// changes when it should, which is the hard direction to notice. The module version ids the Metal key
    /// already carries do not help here, because this is not an engine assembly and nothing about it moves when
    /// the engine rebuilds.
    /// </para>
    /// <para>
    /// READ OFF THE LOADED ASSEMBLY RATHER THAN OUT OF THE PROPS FILE, so it cannot drift from what is actually
    /// running. A number lifted from <c>Directory.Packages.props</c> at build time describes what the build asked
    /// for, and a machine restoring from a different feed, a floating range or a hand-dropped binary would make
    /// that a claim rather than a fact. <c>typeof(SpirvCompilation).Assembly</c> is the assembly whose code is
    /// about to run, which is the same reasoning that put the two module version ids in the Metal key.
    /// </para>
    /// <para>
    /// BOTH VERSIONS, BECAUSE EITHER ONE ALONE CAN STAND STILL ACROSS A PACKAGE BUMP. The assembly version is
    /// what the loader binds on, and a publisher is free to hold it constant across a patch release. The
    /// informational version is the NuGet package version plus the source commit it was built from, which is the
    /// finer of the two and the one that moves on every rebuild of the package, but it is a free-text attribute a
    /// publisher can also omit. Hashing both costs one short string and means the key moves whichever of the two
    /// the publisher moved. The versions the engine has actually shipped under are readable proof that neither is
    /// redundant. The outgoing toolchain is the clearer example, because its publisher moved the two
    /// independently: <c>1.0.14</c> carried assembly <c>1.0.14.3</c> and package <c>1.0.14+3c482d30de</c>, and
    /// <c>1.0.15</c> carried assembly <c>1.0.15.1</c> and package <c>1.0.15+a872acfa33</c>. Silk.NET keeps them
    /// in step today (<c>2.23.0</c> carries assembly <c>2.23.0.0</c> and package <c>2.23.0+9460514</c>), which
    /// is a property of one publisher's current habit and not a guarantee, so both stay in the token.
    /// </para>
    /// <para>
    /// THE MANAGED ASSEMBLY IS A PROXY FOR THE NATIVE BLOB, AND THE SWAP MADE THAT PROXY WEAKER. The native
    /// libraries (<c>libshaderc_shared</c> and <c>libspirv-cross</c>) are where glslang and SPIRV-Cross actually
    /// live, and neither carries a version this process can read: both are loaded by <c>[DllImport]</c> through
    /// a managed wrapper that reports nothing about them.
    /// <para>
    /// Until 18.0.0 the proxy held by construction, because the managed wrapper and the native binary shipped
    /// as ONE package, so the blob beside a given assembly was the one that package built. It does not hold by
    /// construction any more: <c>Silk.NET.Shaderc.Native</c> and <c>Silk.NET.SPIRV.Cross.Native</c> are
    /// SEPARATE package ids from their managed halves, so a version pinned on one and not the other is
    /// expressible in <c>Directory.Packages.props</c> and would leave this token standing still while the
    /// emitted bytes moved. What holds it now is a convention rather than a package boundary: the five ids move
    /// together on one Silk.NET line, and <c>Directory.Packages.props</c> pins all five to the same version on
    /// purpose. Splitting them is the change that breaks this, so do not.
    /// </para>
    /// What neither version of the argument covers is somebody replacing a native binary in an output directory
    /// by hand, which is a deliberate act on a developer machine and is exactly what the cache's disable words
    /// exist for.
    /// </para>
    /// <para>
    /// IT LIVES IN <c>KhaozEngine.Gpu</c> BECAUSE THE TOOLCHAIN EDGE DOES, decision P2, and
    /// <c>ArchitectureTests.ThirdPartyHomes</c> enforces that. Both backends that hash this consume it as a
    /// STRING across <c>InternalsVisibleTo</c>, so neither takes a toolchain type reference in its own IL, which
    /// is the same contract <see cref="SpirvCrossCompile"/> keeps for its signatures and which the built-IL walk
    /// would otherwise fail.
    /// </para>
    /// </summary>
    internal static class SpirvToolchainVersion
    {
        /// <summary>What an unreadable version reads as. A named token rather than an empty string, so a key that
        /// hashed one is still a legal key and a diagnostic that prints one says what happened.</summary>
        internal const string Unknown = "unknown";

        /// <summary>
        /// The identity of the shader toolchain this process loaded, as a stable one-line token. Computed once,
        /// on first use, because an assembly's own metadata cannot change while it is loaded.
        /// <para>
        /// BOTH HALVES, because they are two packages now and either can move without the other. The front end
        /// (<c>Silk.NET.Shaderc</c>, glslang) and the back end (<c>Silk.NET.SPIRV.Cross</c>) shipped as one
        /// package until 18.0.0, and a key that named only one of them would answer with the previous emitter's
        /// output for every program after an upgrade that moved just the other.
        /// </para>
        /// </summary>
        internal static string Identity { get; } =
            For(typeof(Shaderc).Assembly) + "|" + For(typeof(Cross).Assembly);

        /// <summary>
        /// The same token for any assembly, so the contribution is testable without building two engines against
        /// two packages. <paramref name="assembly"/> is a BCL type on purpose: nothing about this member's shape
        /// names a toolchain type, so a caller in a backend that declares no toolchain package can reach it.
        /// </summary>
        /// <param name="assembly">The assembly to describe.</param>
        internal static string For(Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly);

            string name = assembly.GetName().Name ?? Unknown;
            string assemblyVersion = assembly.GetName().Version?.ToString() ?? Unknown;
            string package =
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Unknown;

            return name + ";assembly=" + assemblyVersion + ";package=" + package;
        }
    }
}
