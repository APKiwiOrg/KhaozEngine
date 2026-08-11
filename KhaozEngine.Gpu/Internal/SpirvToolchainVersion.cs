using System;
using System.Reflection;
using Veldrid.SPIRV;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// THE VERSION OF THE THING THAT ACTUALLY PRODUCES THE BYTES, as one string a shader-cache key can hash
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/610">#610</see>). Every option pin in this
    /// engine freezes what the toolchain is ASKED for, and each of their headers says so in as many words. What
    /// freezes the emitted text itself is the <c>Veldrid.SPIRV</c> package, which carries glslang for the front
    /// end and SPIRV-Cross for both back ends, and no pin reaches it.
    ///
    /// <para>
    /// WHY A KEY NEEDS IT. The engine version partitions the caches across releases, because it is both a key
    /// component and a cache-directory segment, so a released bump can never inherit an entry. Within ONE engine
    /// version it does not: a developer bumping <c>Veldrid.SPIRV</c> in <c>Directory.Packages.props</c> without
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
    /// redundant: <c>1.0.14</c> carries assembly <c>1.0.14.3</c> and package <c>1.0.14+3c482d30de</c>, and
    /// <c>1.0.15</c> carries assembly <c>1.0.15.1</c> and package <c>1.0.15+a872acfa33</c>.
    /// </para>
    /// <para>
    /// THE MANAGED ASSEMBLY IS A PROXY FOR THE NATIVE <c>libveldrid-spirv</c>, AND THAT IS WORTH SAYING PLAINLY,
    /// because the native library is where glslang and SPIRV-Cross actually live and it carries no version this
    /// process can read: it is loaded by <c>[DllImport]</c> through the managed wrapper and exports three entry
    /// points, none of which reports a version. The proxy holds because the two ship as ONE package, so the
    /// native binary beside a given managed assembly is the one that package built. What it does not cover is
    /// somebody replacing the native binary in an output directory by hand, which is a deliberate act on a
    /// developer machine and is exactly what the cache's disable words exist for.
    /// </para>
    /// <para>
    /// IT LIVES IN <c>KhaozEngine.Gpu</c> BECAUSE THE <c>Veldrid.SPIRV</c> EDGE DOES, decision P2, and
    /// <c>ArchitectureTests.ThirdPartyHomes</c> enforces that. Both backends that hash this consume it as a
    /// STRING across <c>InternalsVisibleTo</c>, so neither takes a Veldrid type reference in its own IL, which is
    /// the same Veldrid-free contract <see cref="SpirvCrossCompile"/> keeps for its signatures and which the
    /// built-IL walk would otherwise fail.
    /// </para>
    /// </summary>
    internal static class SpirvToolchainVersion
    {
        /// <summary>What an unreadable version reads as. A named token rather than an empty string, so a key that
        /// hashed one is still a legal key and a diagnostic that prints one says what happened.</summary>
        internal const string Unknown = "unknown";

        /// <summary>
        /// The identity of the <c>Veldrid.SPIRV</c> this process loaded, as a stable one-line token. Computed
        /// once, on first use, because reading an assembly's own metadata cannot change while it is loaded.
        /// </summary>
        internal static string Identity { get; } = For(typeof(SpirvCompilation).Assembly);

        /// <summary>
        /// The same token for any assembly, so the contribution is testable without building two engines against
        /// two packages. <paramref name="assembly"/> is a BCL type on purpose: nothing about this member's shape
        /// names a Veldrid type, so a caller in a Veldrid-free backend can reach it.
        /// </summary>
        /// <param name="assembly">The assembly to describe.</param>
        internal static string For(Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly);

            string assemblyVersion = assembly.GetName().Version?.ToString() ?? Unknown;
            string package =
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Unknown;

            return "veldrid-spirv;assembly=" + assemblyVersion + ";package=" + package;
        }
    }
}
