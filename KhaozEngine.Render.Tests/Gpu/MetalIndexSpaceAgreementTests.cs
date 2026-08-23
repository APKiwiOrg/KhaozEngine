using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE TWO SPELLINGS OF METAL'S THREE ARGUMENT TABLES, PINNED AGAINST EACH OTHER.
    /// <c>MslIndexRemap.SpaceFor</c> lives in <c>KhaozEngine.Gpu</c>, beside the emitter that installs the
    /// authored indices, and <c>MetalIndexSpaces.For</c> lives in the backend, beside the binder that reads them.
    /// Two enums and two switches, because neither package should have to reach into the other for a three-way
    /// mapping, and because the backend's copy carries the space-versus-kind CHECK that the emitter's does not
    /// need.
    ///
    /// <para>
    /// THE DUPLICATION IS THE RISK AND THIS IS THE PRICE PAID FOR IT. A kind mapped to the buffer table on one
    /// side and the texture table on the other authors an index in one space and binds it in another, which is a
    /// resource read from a slot nothing wrote: black, silent, and on Metal only. So the two are compared over
    /// EVERY <c>GpuResourceKind</c> rather than over the ones anyone happened to think of, which also makes a new
    /// kind a red row here until both sides answer for it.
    /// </para>
    /// </summary>
    public sealed class MetalIndexSpaceAgreementTests
    {
        [Fact]
        public void EveryResourceKind_LandsInTheSameArgumentTableOnBothSides()
        {
            GpuResourceKind[] kinds = Enum.GetValues<GpuResourceKind>();
            Assert.True(kinds.Length >= 6, "the kind enum shrank, so this comparison covers less than it did.");

            foreach (GpuResourceKind kind in kinds)
            {
                MslIndexSpace authored = MslIndexRemap.SpaceFor(kind);
                MetalIndexSpace bound = MetalIndexSpaces.For(kind);

                Assert.Equal(authored.ToString(), bound.ToString());
                Assert.True(bound.MatchesKind(kind),
                    $"{kind} does not belong in the {bound.Word()} table it is mapped to.");
            }
        }

        /// <summary>
        /// AND THE THREE MEMBERS LINE UP ONE FOR ONE, so the comparison above is a mapping rather than a
        /// coincidence of names on a shorter enum.
        /// </summary>
        [Fact]
        public void TheTwoSpaceEnums_DeclareTheSameThreeMembers()
        {
            Assert.Equal(
                Enum.GetNames<MslIndexSpace>().OrderBy(n => n, StringComparer.Ordinal),
                Enum.GetNames<MetalIndexSpace>().OrderBy(n => n, StringComparer.Ordinal));
        }

        /// <summary>
        /// THE HELPER-BUFFER REFUSAL, DRIVEN. SPIRV-Cross adds buffer arguments of its own for a handful of
        /// features, numbered from the TOP of the argument table, which is where decision M-B2 pins the vertex
        /// streams. Such an argument carries no <c>(set, binding)</c>, so it is in no layout, in no binding
        /// table, and invisible to the pipeline-creation collision assertion. Before row 10 it was caught by
        /// accident, because the argument parse could not read an <c>_&lt;id&gt;</c> out of its name. Nothing
        /// parses an argument any more, so the refusal is asked for directly.
        /// <para>
        /// A RUNTIME-SIZED ARRAY IS THE CHEAPEST WAY IN. <c>v.length()</c> on an unsized storage-buffer member
        /// makes MSL need a buffer-size buffer, because the length is not in the type.
        /// </para>
        /// </summary>
        [Fact]
        public void AShaderNeedingASpirvCrossHelperBuffer_IsRefused()
        {
            const string kernel = @"#version 450
layout(local_size_x = 64) in;
layout(set = 0, binding = 0) buffer Data { float v[]; };
void main() { v[gl_GlobalInvocationID.x] = float(v.length()); }
";

            ShaderValidationException error = Assert.Throws<ShaderValidationException>(
                () => MetalShaderBuild.Compute(kernel, null, "runtime-array"));

            Assert.Contains("buffer-size buffer", error.Message, StringComparison.Ordinal);
            Assert.Contains("M-B2", error.Message, StringComparison.Ordinal);
        }

        /// <summary>The control: the same kernel with a SIZED array needs no helper buffer and builds.</summary>
        [Fact]
        public void TheSameKernelWithASizedArray_Builds()
        {
            const string kernel = @"#version 450
layout(local_size_x = 64) in;
layout(set = 0, binding = 0) buffer Data { float v[256]; };
void main() { v[gl_GlobalInvocationID.x] = 1.0; }
";

            (MetalMslProgram program, uint x, uint _, uint __) = MetalShaderBuild.Compute(kernel, null, "sized");
            Assert.Equal(64u, x);
            Assert.True(program.Table.TryGetIndex(0, 0, MetalShaderStage.Compute, out MetalIndexTableEntry entry));
            Assert.Equal(new MetalIndexTableEntry(MetalIndexSpace.Buffer, 0), entry);
        }
    }
}
