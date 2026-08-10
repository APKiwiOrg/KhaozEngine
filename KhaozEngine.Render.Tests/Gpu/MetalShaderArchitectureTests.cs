using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using KhaozEngine.Gpu.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION M-S1's STRUCTURAL HALF, AND THE THIRD ARM OF AN ARCHITECTURE TEST THE PROGRAM HAS RUN TWICE
    /// BEFORE: <c>KhaozEngine.Gpu.Metal</c> names the shader toolchain's front end and its MSL members, and never
    /// an HLSL one. Section 12.1 of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    ///
    /// <para><b>WHY THIS IS A MEMBER CHECK WHERE THE VULKAN ONE IS A TYPE CHECK</b>, which is the whole reason it
    /// is a separate file rather than a row added to <see cref="VulkanShaderFrontEndOnlyTests"/>. That backend
    /// consumes SPIR-V and must not name <c>SpirvCrossCompile</c> AT ALL, so a type reference settles it. This one
    /// consumes MSL, so it names that type legitimately and the question moves down a level: WHICH members. A
    /// type-level test here would pass while the backend cross-compiled the whole corpus to HLSL and threw it
    /// away.
    /// </para>
    /// <para><b>WHAT GOING WRONG WOULD LOOK LIKE.</b> Not a crash. <c>VertexFragmentToHlsl</c> sits one letter
    /// away from <c>VertexFragmentToMsl</c> across the same <c>InternalsVisibleTo</c> grant, returns the same
    /// mirror type, and compiles. What follows is a shader set built from HLSL text handed to
    /// <c>newLibraryWithSource:</c>, which fails loudly, or worse, a binding table parsed out of HLSL, which does
    /// not: the depth-matched argument walk finds no <c>[[buffer(n)]]</c> attributes at all and every element
    /// reads as unreferenced, which binds NOTHING and renders black with no error anywhere. That is the
    /// everything-compiles-every-pixel-is-wrong class this row's whole test plan is built around, arriving
    /// through an autocomplete.
    /// </para>
    /// <para><b>OVER THE BUILT IL, not the source text</b>, for the reason the Vulkan test gives: a member this
    /// assembly names appears in its <c>MemberRef</c> table whatever alias, <c>using</c> or fully qualified form
    /// the source used, and a grep would be one <c>global::</c> away from a false pass.</para>
    /// </summary>
    public sealed class MetalShaderArchitectureTests
    {
        readonly ITestOutputHelper _output;

        public MetalShaderArchitectureTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// The HLSL members of the shared back end. Named as strings rather than by <c>nameof</c> so the list can
        /// outlive a rename loudly, with <see cref="EveryHlslMemberNamed_StillExists"/> stopping it from decaying
        /// into a list of names nothing has, which would make the assertion a tautology.
        /// </summary>
        static readonly string[] HlslMembers =
        [
            "VertexFragmentToHlsl",
            "ComputeToHlsl",
            "GlslPairToHlsl",
            "GlslComputeToHlsl",
        ];

        [Fact]
        public void TheMetalBackend_NamesNoHlslMember()
        {
            string[] named = MembersNamedOn(nameof(SpirvCrossCompile))
                .Where(n => HlslMembers.Contains(n, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.True(named.Length == 0,
                "KhaozEngine.Gpu.Metal names the HLSL half of the cross-compile back end: ["
                + string.Join(", ", named) + "]. Metal consumes MSL, and the HLSL members sit one letter away "
                + "across the same InternalsVisibleTo grant and return the same mirror type, so this compiles. "
                + "What follows is either a library built from HLSL text, which fails loudly, or a binding table "
                + "parsed out of HLSL, which does not: the argument walk finds no [[buffer(n)]] attributes, every "
                + "element reads as unreferenced, nothing is bound and the frame is black with no error anywhere.");
        }

        /// <summary>
        /// AND IT DOES NAME THE MSL MEMBERS AND THE FRONT END, which is the assertion the one above most needs
        /// and would most easily lack. A metadata read that silently found nothing would pass forever.
        /// </summary>
        [Fact]
        public void TheMetalBackend_DoesNameTheMslMembersAndTheFrontEnd()
        {
            string[] onCrossCompile = MembersNamedOn(nameof(SpirvCrossCompile)).ToArray();
            _output.WriteLine("named on SpirvCrossCompile: " + string.Join(", ", onCrossCompile.Distinct()));

            Assert.Contains("VertexFragmentToMsl", onCrossCompile, StringComparer.Ordinal);
            Assert.Contains("ComputeToMsl", onCrossCompile, StringComparer.Ordinal);

            string[] types = TypeReferencesOfTheMetalBackend().ToArray();
            Assert.Contains(nameof(SpirvFrontEnd), types, StringComparer.Ordinal);
            Assert.Contains(nameof(SpirvLocalSize), types, StringComparer.Ordinal);

            // The id-keyed join's key reader, which is what section 2.2b's ruling rests on. Naming it is what
            // says the binding table is READ off the decorations rather than counted.
            Assert.Contains(nameof(SpirvResourceDecorations), types, StringComparer.Ordinal);
        }

        /// <summary>
        /// AND IT NAMES ITS OWN PIN RATHER THAN THE OTHER TARGET'S. <see cref="HlslCrossCompilePin"/> is a type,
        /// so this one is a type-level check, and it is the cheapest way to catch a cache key or an options set
        /// built from the wrong half of the toolchain.
        /// </summary>
        [Fact]
        public void TheMetalBackend_NamesItsOwnPinAndNotTheHlslOne()
        {
            string[] types = TypeReferencesOfTheMetalBackend().ToArray();

            Assert.Contains(nameof(MslCrossCompilePin), types, StringComparer.Ordinal);
            Assert.Contains(nameof(SpirvFrontEndPin), types, StringComparer.Ordinal);
            Assert.DoesNotContain(nameof(HlslCrossCompilePin), types, StringComparer.Ordinal);
        }

        /// <summary>
        /// THE FORBIDDEN LIST NAMES REAL MEMBERS, so a rename cannot quietly empty it. Same guard the Vulkan
        /// architecture test puts on its own list, and for the same reason.
        /// </summary>
        [Fact]
        public void EveryHlslMemberNamed_StillExists()
        {
            var declared = typeof(SpirvCrossCompile)
                .GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic)
                .Select(m => m.Name)
                .ToArray();

            foreach (string name in HlslMembers)
            {
                Assert.True(declared.Contains(name, StringComparer.Ordinal),
                    $"The forbidden list names {name}, which SpirvCrossCompile does not declare. Either it was "
                    + "renamed and this list was not, or the HLSL half lost a member. Both are edits somebody has "
                    + "to make here deliberately, because a list of names nothing has asserts nothing.");
            }
        }

        // Every member this assembly REFERENCES whose declaring type has the given simple name. Read off the
        // MemberRef table, whose Parent for an ordinary external call is the TypeRef of the declaring type.
        static IEnumerable<string> MembersNamedOn(string declaringTypeName)
        {
            using MetadataReaderHolder holder = OpenTheMetalBackend();
            MetadataReader metadata = holder.Metadata;

            var names = new List<string>();
            foreach (MemberReferenceHandle handle in metadata.MemberReferences)
            {
                MemberReference member = metadata.GetMemberReference(handle);
                if (member.Parent.Kind != HandleKind.TypeReference) continue;

                TypeReference parent = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
                if (!string.Equals(metadata.GetString(parent.Name), declaringTypeName, StringComparison.Ordinal))
                    continue;

                names.Add(metadata.GetString(member.Name));
            }
            return names;
        }

        static IEnumerable<string> TypeReferencesOfTheMetalBackend()
        {
            using MetadataReaderHolder holder = OpenTheMetalBackend();
            MetadataReader metadata = holder.Metadata;

            var names = new List<string>();
            foreach (TypeReferenceHandle handle in metadata.TypeReferences)
                names.Add(metadata.GetString(metadata.GetTypeReference(handle).Name));

            Assert.NotEmpty(names);
            return names;
        }

        static MetadataReaderHolder OpenTheMetalBackend()
        {
            string path = typeof(KhaozEngine.Gpu.Metal.KhaozEngineMetal).Assembly.Location;
            Assert.True(File.Exists(path),
                "The Metal backend assembly is not on disk at " + path + ", so its metadata cannot be read. A "
                + "single-file or in-memory host would report an empty Location, and these tests would then "
                + "assert nothing rather than fail, which is why the path is checked first.");

            return new MetadataReaderHolder(path);
        }

        // The PEReader and its stream have to outlive the MetadataReader, which is a ref struct borrowed from
        // them, so they are held together and disposed together rather than in the caller's finally.
        sealed class MetadataReaderHolder : IDisposable
        {
            readonly FileStream _stream;
            readonly PEReader _pe;

            internal MetadataReaderHolder(string path)
            {
                _stream = File.OpenRead(path);
                _pe = new PEReader(_stream);
                Metadata = _pe.GetMetadataReader();
            }

            internal MetadataReader Metadata { get; }

            public void Dispose()
            {
                _pe.Dispose();
                _stream.Dispose();
            }
        }
    }
}
