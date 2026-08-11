using System;
using System.Reflection;
using System.Reflection.Emit;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE CROSS-COMPILER'S OWN VERSION IS IN BOTH SHADER-CACHE KEYS
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/610">#610</see>). Every option pin freezes
    /// what the toolchain is ASKED for. What freezes the emitted text is the <c>Veldrid.SPIRV</c> package, which
    /// is not an engine assembly, so neither the engine version nor the Metal key's module version ids move when
    /// it does. A package bump without an engine bump used to leave every cached program on both backends
    /// answering with the previous cross-compiler's output.
    ///
    /// <para>
    /// BOTH BACKENDS IN ONE FILE, because it is one claim with two implementations and the failure the issue
    /// describes is the two drifting apart. It belongs in both keys or in neither.
    /// </para>
    /// <para>
    /// DEVICE-FREE AND PACKAGE-FREE: the identity is passed IN, so the claim is asserted without restoring two
    /// versions of <c>Veldrid.SPIRV</c>. That the real identity feeds the shipped overload is asserted
    /// separately, on each key, which is the half a fabricated input cannot cover.
    /// </para>
    /// </summary>
    public sealed class ShaderKeyToolchainTests
    {
        const string VertGlsl = "#version 450\nvoid main() { gl_Position = vec4(0); }\n";
        const string FragGlsl = "#version 450\nlayout(location=0) out vec4 c;\nvoid main() { c = vec4(1); }\n";

        static readonly string[] Program = { VertGlsl, FragGlsl };

        // ---- the identity itself -------------------------------------------------------------------------

        /// <summary>
        /// IT IS READ OFF THE LOADED ASSEMBLY, not out of the props file and not out of a constant. Feeding a
        /// different assembly produces a different token, which is what proves the value is derived rather than
        /// typed, and the shipped one names a real version rather than the unknown fallback.
        /// </summary>
        [Fact]
        public void TheToolchainIdentity_IsReadOffTheAssemblyThatRuns()
        {
            string identity = SpirvToolchainVersion.Identity;

            Assert.StartsWith("veldrid-spirv;", identity, StringComparison.Ordinal);
            Assert.DoesNotContain(SpirvToolchainVersion.Unknown, identity, StringComparison.Ordinal);

            // BOTH halves are in it, because either one alone can stand still across a package bump: the
            // assembly version is what the loader binds on and a publisher may hold it across a patch, and the
            // informational version carries the package version plus the commit but is free text.
            Assert.Contains(";assembly=", identity, StringComparison.Ordinal);
            Assert.Contains(";package=", identity, StringComparison.Ordinal);

            // Derived, not typed: another assembly describes itself rather than the cross-compiler.
            Assert.NotEqual(identity, SpirvToolchainVersion.For(typeof(object).Assembly));
        }

        /// <summary>
        /// An assembly that declares no informational version reads as the NAMED fallback rather than as an empty
        /// string, so a key that hashed one is still a legal key and a diagnostic that printed one says what
        /// happened. A dynamic assembly is the readiest example of one: it carries a version number and no
        /// attributes at all.
        /// </summary>
        [Fact]
        public void AnAssemblyWithNoDeclaredPackageVersion_ReadsAsTheNamedFallback()
        {
            Assert.Throws<ArgumentNullException>(() => SpirvToolchainVersion.For(null!));

            AssemblyBuilder bare = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("ke-toolchain-probe"), AssemblyBuilderAccess.Run);

            Assert.Equal(
                "veldrid-spirv;assembly=0.0.0.0;package=" + SpirvToolchainVersion.Unknown,
                SpirvToolchainVersion.For(bare));
        }

        // ---- the Metal key -------------------------------------------------------------------------------

        [Fact]
        public void TheMetalKey_MovesWhenTheCrossCompilerVersionDoes()
        {
            Guid metal = MetalShaderKey.MetalModuleId;
            Guid gpu = MetalShaderKey.GpuModuleId;

            string baseline = MetalShaderKey.For(metal, gpu, "veldrid-spirv;assembly=1.0.15.1", Program);
            string bumped = MetalShaderKey.For(metal, gpu, "veldrid-spirv;assembly=1.0.16.0", Program);

            Assert.Equal(baseline, MetalShaderKey.For(metal, gpu, "veldrid-spirv;assembly=1.0.15.1", Program));
            Assert.NotEqual(baseline, bumped);

            // The shipped overload hashes the identity of the package this process actually loaded.
            Assert.Equal(
                MetalShaderKey.For(metal, gpu, SpirvToolchainVersion.Identity, Program),
                MetalShaderKey.For(Program));
        }

        [Fact]
        public void TheMetalKey_RefusesAToolchainThatIsNotOne()
        {
            Assert.Throws<ArgumentNullException>(
                () => MetalShaderKey.For(Guid.Empty, Guid.Empty, null!, Program));
            Assert.Throws<ArgumentException>(
                () => MetalShaderKey.For(Guid.Empty, Guid.Empty, "   ", Program));
        }

        // ---- the Direct3D 11 key -------------------------------------------------------------------------

        [Fact]
        public void TheDirect3D11Key_MovesWhenTheCrossCompilerVersionDoes()
        {
            const D3D11ShaderStage stage = D3D11ShaderStage.Vertex;

            string baseline = D3D11ShaderKey.For(stage, 0u, "veldrid-spirv;assembly=1.0.15.1", Program);
            string bumped = D3D11ShaderKey.For(stage, 0u, "veldrid-spirv;assembly=1.0.16.0", Program);

            Assert.Equal(baseline, D3D11ShaderKey.For(stage, 0u, "veldrid-spirv;assembly=1.0.15.1", Program));
            Assert.NotEqual(baseline, bumped);

            // Every stage of the program moves, not just the one that named the flag.
            Assert.NotEqual(
                D3D11ShaderKey.For(D3D11ShaderStage.Fragment, 0u, "veldrid-spirv;assembly=1.0.15.1", Program),
                D3D11ShaderKey.For(D3D11ShaderStage.Fragment, 0u, "veldrid-spirv;assembly=1.0.16.0", Program));

            // The shipped overload hashes the identity of the package this process actually loaded.
            Assert.Equal(
                D3D11ShaderKey.For(stage, 0u, SpirvToolchainVersion.Identity, Program),
                D3D11ShaderKey.For(stage, 0u, VertGlsl, FragGlsl));
        }

        [Fact]
        public void TheDirect3D11Key_RefusesAToolchainThatIsNotOne()
        {
            Assert.Throws<ArgumentNullException>(
                () => D3D11ShaderKey.For(D3D11ShaderStage.Vertex, 0u, null!, Program));
            Assert.Throws<ArgumentException>(
                () => D3D11ShaderKey.For(D3D11ShaderStage.Vertex, 0u, " ", Program));
        }

        // ---- the schema tags -----------------------------------------------------------------------------

        /// <summary>
        /// BOTH SCHEMA TAGS MOVED WITH THE FIELD. A reshaped key that kept its tag could collide with an entry
        /// hashed under the old shape, which is the one thing a content-addressed cache cannot recover from.
        /// </summary>
        [Fact]
        public void BothSchemaTags_NameTheReshapedKey()
        {
            Assert.Equal("khaozengine-metal-program-v3", MetalShaderKey.Schema);
            Assert.Equal("khaozengine-d3d11-dxbc-v3", D3D11ShaderKey.Schema);
        }
    }
}
