using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION V-S3's STRUCTURAL HALF: <c>KhaozEngine.Gpu.Vulkan</c> depends on the shader toolchain's FRONT END
    /// only, and names no member of the SPIRV-Cross BACK END. Section 12.3 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>, work-breakdown row 16
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/526).
    ///
    /// <para><b>WHY THE SPLIT IS WORTH ASSERTING RATHER THAN INTENDING.</b> Vulkan consumes SPIR-V, so nothing on
    /// its shader path is cross-compiled, and the eventual SPIRV-Cross replacement
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/462) is therefore a change to ONE HALF of one file with
    /// one consumer family, evaluated against that family's own goldens rather than against Direct3D 11's 36
    /// committed ones. That property survives only while this backend genuinely does not reach the back end, and
    /// the tempting shortcut is one line: <c>SpirvCrossCompile.GlslPairToHlsl</c> is right there across the same
    /// <c>InternalsVisibleTo</c> grant and would compile. Nothing else in the net would notice, because both
    /// halves live in <c>KhaozEngine.Gpu</c>, so the assembly-reference scans in <c>GpuPublicApiTests</c> read
    /// identically either way.</para>
    ///
    /// <para><b>SO THE CHECK IS OVER THE BUILT IL, not over the source text.</b> A type this assembly names from
    /// another assembly appears in its <c>TypeRef</c> table, whatever alias, <c>using</c> or fully qualified form
    /// the source used. A grep would be one <c>global::</c> away from a false pass.</para>
    ///
    /// <para><b>AND WHAT IT CANNOT SEE, stated so the property is not read as closed.</b> A reference table names
    /// what this assembly names DIRECTLY, so an INDIRECT reach stays invisible: a new helper in
    /// <c>KhaozEngine.Gpu</c> that calls <c>SpirvCrossCompile</c> itself and hands back primitives would leave
    /// this backend naming only the helper. That is a deliberate two-file edit by somebody who wanted the back
    /// end from here, rather than the one-line shortcut across an existing <c>InternalsVisibleTo</c> grant that
    /// this test exists to catch, and the one-line shortcut is the failure that actually happens.</para>
    /// </summary>
    public sealed class VulkanShaderFrontEndOnlyTests
    {
        /// <summary>
        /// The back end's types. Named as strings rather than by <c>typeof</c>, so the list can outlive a rename
        /// loudly: <see cref="EveryBackEndTypeNamed_StillExists"/> is what stops it decaying into a list of names
        /// nothing has, which would turn the assertion below into a tautology.
        /// </summary>
        static readonly string[] BackEndTypes =
        [
            // The SPIRV-Cross seat itself. VertexFragmentToHlsl, ComputeToHlsl and both GLSL conveniences are
            // members of it, so naming ANY of them puts this type in the reference table.
            "SpirvCrossCompile",
            // Its pin, which only the back end's options live in.
            "HlslCrossCompilePin",
            // And the back end's own result mirrors, which nothing but a cross-compile produces.
            "CrossCompiledPair",
            "CrossCompiledCompute",
            "ShaderReflection",
        ];

        [Fact]
        public void TheVulkanBackend_NamesNoCrossCompileBackEndType()
        {
            string[] named = TypeReferencesOfTheVulkanBackend()
                .Where(n => BackEndTypes.Contains(n, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.True(named.Length == 0,
                "KhaozEngine.Gpu.Vulkan names the cross-compile BACK END: [" + string.Join(", ", named) + "]. "
                + "Vulkan consumes SPIR-V, so nothing on this backend's shader path is cross-compiled and it must "
                + "reach SpirvFrontEnd alone (decision V-S3). Whatever this call was reaching for, the front end "
                + "or the seam already carries it: the pipeline's vertex input comes from the caller's own "
                + "GpuVertexLayoutDescription and its bindings from the layout array, so a reflection read here "
                + "would be a second source of truth for facts the seam already has.");
        }

        /// <summary>
        /// AND IT DOES NAME THE FRONT END, which is the assertion the test above most needs and would most easily
        /// lack. A metadata read that silently found nothing would pass forever, and so would a split that left
        /// the Vulkan backend compiling its own SPIR-V somewhere else.
        /// </summary>
        [Fact]
        public void TheVulkanBackend_DoesNameTheFrontEnd()
        {
            string[] named = TypeReferencesOfTheVulkanBackend().ToArray();

            Assert.Contains(nameof(SpirvFrontEnd), named, StringComparer.Ordinal);
            Assert.Contains(nameof(SpirvLocalSize), named, StringComparer.Ordinal);
        }

        /// <summary>
        /// THE FORBIDDEN LIST NAMES REAL TYPES, so a rename cannot quietly empty it. Same guard
        /// <c>VulkanRecordingUnreachabilityTests</c> puts on its own list of names, and for the same reason.
        /// </summary>
        [Fact]
        public void EveryBackEndTypeNamed_StillExists()
        {
            Type[] all = typeof(SpirvCrossCompile).Assembly.GetTypes();

            foreach (string name in BackEndTypes)
            {
                Assert.True(all.Any(t => string.Equals(t.Name, name, StringComparison.Ordinal)),
                    $"The back-end list names {name}, which no type in KhaozEngine.Gpu has. Either it was renamed "
                    + "and this list was not, or the back end lost a type. Both are edits somebody has to make "
                    + "here deliberately, because a list of names nothing has asserts nothing.");
            }
        }

        // Every type this assembly REFERENCES from another assembly, by simple name. Read off the TypeRef table
        // rather than through reflection, because reflection can only enumerate the types an assembly DEFINES and
        // the whole question here is which types it names.
        static IEnumerable<string> TypeReferencesOfTheVulkanBackend()
        {
            string path = typeof(KhaozEngine.Gpu.Vulkan.KhaozEngineVulkan).Assembly.Location;
            Assert.True(File.Exists(path),
                "The Vulkan backend assembly is not on disk at " + path + ", so its metadata cannot be read. A "
                + "single-file or in-memory host would report an empty Location, and this test would then assert "
                + "nothing rather than fail, which is why the path is checked first.");

            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            MetadataReader metadata = pe.GetMetadataReader();

            var names = new List<string>();
            foreach (TypeReferenceHandle handle in metadata.TypeReferences)
                names.Add(metadata.GetString(metadata.GetTypeReference(handle).Name));

            Assert.NotEmpty(names);
            return names;
        }
    }
}
