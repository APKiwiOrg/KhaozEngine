using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION M-B2 AND THE PLAN IT NUMBERS, device-free, on every <c>dotnet test</c>. Section 8.3 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>, work-breakdown row 11
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/577).
    ///
    /// <para><b>THE CORPUS ROW IS THE ONE THE DESIGN NAMES, and it is a MEASUREMENT rather than a
    /// restatement.</b> 8.3 argues that vertex streams pinned at the top cannot collide with resource buffers
    /// growing from 0, and that argument is easy to believe from the arithmetic, which is exactly how an
    /// inherited assumption survives the change that breaks it. So the assertion is taken against the indices the
    /// emission actually chose, over every shipped program, and it REPORTS the headroom so a corpus creeping
    /// upward is visible before it collides rather than after.</para>
    ///
    /// <para><b>THE INDICES COME OUT OF THE TABLE AND NEVER OUT OF DECLARED VISIBILITY.</b>
    /// <c>GpuResourceLayoutElement.Stages</c> says which stages an element is MEANT to be visible to, and
    /// SPIRV-Cross omits an argument a stage never references, so an element declared vertex-visible and never
    /// read contributes no index and cannot collide with anything. Row 10's handoff pins that distinction and
    /// this file is where it is honoured.</para>
    ///
    /// <para><b>THE PLAN ROWS BELOW ARE THE OTHER HALF OF THE SAME PROPERTY.</b> The scheme only works if the
    /// <c>MTLVertexDescriptor</c>'s layout index and the index a stream is bound at are the same number, so the
    /// plan is asserted to produce ONE buffer index per slot that both the attribute and the stream carry. The
    /// incumbent computes it twice from <c>NonVertexBufferCount</c>, and getting the two out of step is what
    /// binds a vertex buffer where a uniform should be.</para>
    /// </summary>
    public sealed class MetalVertexInputTests
    {
        readonly ITestOutputHelper _output;

        public MetalVertexInputTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// M-B2's NUMBERING, PINNED ONCE FOR BOTH OF ITS READERS. Stream 0 takes the top of the vertex stage's
        /// buffer table and each next one takes the slot below it, so resource buffers growing from 0 upward
        /// cannot collide with them and neither numbering depends on the other's count.
        /// <para>
        /// THIS IS THE ONLY PIN, and it being the only one is the assertion. Rows 11 and 13 each carried their
        /// own copy of it over their own copy of the mapping (the <c>MTLVertexDescriptor</c>'s layout index and
        /// the <c>setVertexBuffers:</c> bind index), which is precisely the shape M-B2 exists to remove: two
        /// independent subtractions agreeing today, with a device that reports NOTHING on the day they stop.
        /// Both call sites now come from <see cref="MetalVertexStreamIndex"/> and both rows' assertions are
        /// merged here. <c>MetalVertexStreamCacheTests</c> is row 13's flush behaviour and points at this row for
        /// the numbers.
        /// </para>
        /// </summary>
        [Fact]
        public void StreamsAreNumberedFromTheTopDownward()
        {
            Assert.Equal(30u, MetalVertexStreamIndex.ForSlot(0));
            Assert.Equal(29u, MetalVertexStreamIndex.ForSlot(1));
            Assert.Equal(28u, MetalVertexStreamIndex.ForSlot(2));

            // The BOTTOM of the table, and the slot past it. A wrapping subtraction answers a plausible-looking
            // huge index here instead of refusing, which is the failure the guarded mapping removes.
            Assert.Equal(0u, MetalVertexStreamIndex.ForSlot(30));
            Assert.Throws<ArgumentOutOfRangeException>(() => MetalVertexStreamIndex.ForSlot(31));

            // A pipeline with no streams occupies nothing, so the lowest occupied index is past the top of the
            // table rather than 30. Without that, a fullscreen pass would reserve buffer 30 for a stream it does
            // not have.
            Assert.Equal(31, MetalVertexStreamIndex.LowestIndexFor(0));
            Assert.Equal(30, MetalVertexStreamIndex.LowestIndexFor(1));
            Assert.Equal(29, MetalVertexStreamIndex.LowestIndexFor(2));
        }

        /// <summary>
        /// M-B2'S ASSERTION OVER THE WHOLE SHIPPED CORPUS, plus the headroom it leaves. This is the row section
        /// 8.3 asks for by name.
        /// </summary>
        [Fact]
        public void NoShippedProgramsVertexStageReachesTheTopPinnedRange()
        {
            var report = new StringBuilder();
            int programs = 0, vertexBufferEntries = 0, highest = -1;
            string highestProgram = "none";

            foreach (ShippedGraphicsProgram program in ShippedShaderPrograms.GraphicsPrograms())
            {
                MetalMslProgram built = MetalShaderBuild.Pair(
                    program.VertexGlsl, program.FragmentGlsl, null, program.Name);

                IReadOnlyList<int> indices = MetalVertexPlan.VertexStageBufferIndices(built.Table);
                vertexBufferEntries += indices.Count;
                programs++;

                foreach (int index in indices)
                {
                    // THE LITERAL PROPERTY 8.3 STATES: no index the emission chose reaches 30 downward, so even a
                    // one-stream pipeline built on this program has its stream to itself.
                    Assert.True(index < MetalVertexStreamIndex.ForSlot(0),
                        $"{program.Name}: the emitted vertex function binds a resource at [[buffer({index})]], "
                        + "which is inside the range M-B2 pins vertex streams to.");

                    if (index <= highest) continue;
                    highest = index;
                    highestProgram = program.Name;
                }

                // And the shipped scheme end to end: two streams is the most any renderer declares (the model
                // pass's vertex plus instance buffers), so this is the real pipeline shape rather than the
                // one-stream floor above.
                MetalVertexPlan.RequireNoCollision(2, built.Table, program.Name);
            }

            int headroom = MetalVertexStreamIndex.LowestIndexFor(0) - 1 - highest;
            report.AppendLine($"programs={programs} vertex-stage buffer entries={vertexBufferEntries}");
            report.AppendLine($"highest emitted vertex-stage buffer index={highest} ({highestProgram})");
            report.AppendLine($"streams that could be top-pinned before a collision={headroom}");
            _output.WriteLine(report.ToString());

            Assert.True(programs > 30 && vertexBufferEntries > 0,
                "the shipped-program walk found almost nothing, so the assertion above means nothing:\n" + report);

            // NOT SCRAPING THE BOUNDARY. The largest shipped pipeline declares two vertex streams, so a margin of
            // eight is not a number this row needs, it is the distance at which a corpus creeping upward becomes
            // visible here instead of at a pipeline that suddenly will not create.
            Assert.True(headroom >= 8,
                "the shipped emission now reaches within eight buffer indices of the top-pinned stream range, "
                + "which is close enough that a program adding resource buffers would collide:\n" + report);
        }

        /// <summary>
        /// A vertex-stage resource buffer inside the stream range is refused, with both numbers in the message.
        /// Hand-built, because no shipped program can reach it.
        /// </summary>
        [Fact]
        public void AVertexStageBufferInsideTheStreamRange_IsRefused()
        {
            // One entry, at buffer 30, which is where stream 0 goes.
            MetalShaderIndexTable table = VertexTableAt(30);

            // With no streams there is no collision: nothing occupies the top.
            MetalVertexPlan.RequireNoCollision(0, table, "no streams");

            ShaderValidationException error = Assert.Throws<ShaderValidationException>(
                () => MetalVertexPlan.RequireNoCollision(1, table, "one stream"));

            Assert.Contains("[[buffer(30)]]", error.Message, StringComparison.Ordinal);
            Assert.Contains("one stream", error.Message, StringComparison.Ordinal);

            // The boundary is exact rather than approximate: an entry at 29 collides with two streams and not
            // with one, which is what makes the check about the COUNT rather than about a fixed range.
            MetalShaderIndexTable lower = VertexTableAt(29);
            MetalVertexPlan.RequireNoCollision(1, lower, "one stream");
            Assert.Throws<ShaderValidationException>(
                () => MetalVertexPlan.RequireNoCollision(2, lower, "two streams"));
        }

        /// <summary>
        /// A FRAGMENT-stage buffer at the same index does NOT collide, which is the per-stage half of the
        /// argument: the three argument tables are per stage, and vertex streams exist only on the vertex one.
        /// </summary>
        [Fact]
        public void AFragmentStageBufferAtTheSameIndex_DoesNotCollide()
        {
            // 31 uniform buffers so the one the FRAGMENT stage reads is authored at buffer index 30, which is
            // where stream 0 sits. The vertex stage reads none of them, so the two numberings never meet.
            var kinds = new GpuResourceKind[31];
            Array.Fill(kinds, GpuResourceKind.UniformBuffer);

            MetalShaderIndexTable table = MetalShaderIndexTable.Build(
                Layout(kinds),
                new[]
                {
                    new MetalStageResourceUse(MetalShaderStage.Fragment, new[] { new MslResourceRef(0, 30) }),
                },
                "fragment only");

            MetalVertexPlan.RequireNoCollision(2, table, "two streams");
        }

        /// <summary>More streams than the buffer table has entries is a different failure and says so.</summary>
        [Fact]
        public void MoreStreamsThanTheTableHas_IsRefusedOnItsOwn()
        {
            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
                () => MetalVertexPlan.RequireNoCollision(32, VertexTableAt(0), "too many"));

            Assert.Contains("31", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The plan's numbering: one buffer index per slot, carried by the stream AND by every attribute that
        /// reads it. These being the same number is the whole of what M-B2 owes the descriptor.
        /// </summary>
        [Fact]
        public void EveryAttributeCarriesItsOwnSlotsBufferIndex()
        {
            MetalVertexStream[] streams = MetalVertexPlan.Build(
                new List<GpuVertexLayoutDescription>
                {
                    new(new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                        new GpuVertexElement("Uv", GpuVertexElementFormat.Float2)),
                    new(0, 1, new[] { new GpuVertexElement("InstanceRow0", GpuVertexElementFormat.Float4) }),
                },
                out MetalVertexAttribute[] attributes);

            Assert.Equal(2, streams.Length);
            Assert.Equal(30u, streams[0].BufferIndex);
            Assert.Equal(29u, streams[1].BufferIndex);

            Assert.Equal(3, attributes.Length);
            Assert.Equal(30u, attributes[0].BufferIndex);
            Assert.Equal(30u, attributes[1].BufferIndex);
            Assert.Equal(29u, attributes[2].BufferIndex);

            // THE ATTRIBUTE INDEX COUNTS ACROSS SLOTS. Slot 1's first element is attribute 2, because a GLSL
            // location is one flat sequence over every vertex input the shader declares.
            Assert.Equal(new uint[] { 0, 1, 2 }, attributes.Select(a => a.AttributeIndex).ToArray());

            // Offsets are packed WITHIN a slot, so slot 1 starts at 0 again rather than continuing slot 0's run.
            Assert.Equal(new uint[] { 0, 12, 0 }, attributes.Select(a => a.OffsetBytes).ToArray());
        }

        /// <summary>A zero stride is the packed sum of the slot's elements, and a declared one is kept.</summary>
        [Fact]
        public void AZeroStrideIsComputedAndADeclaredOneIsKept()
        {
            MetalVertexStream[] computed = MetalVertexPlan.Build(
                new List<GpuVertexLayoutDescription>
                {
                    new(new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                        new GpuVertexElement("Uv", GpuVertexElementFormat.Float2)),
                },
                out _);

            Assert.Equal(20u, computed[0].Stride);

            MetalVertexStream[] declared = MetalVertexPlan.Build(
                new List<GpuVertexLayoutDescription>
                {
                    new(32, 0, new[] { new GpuVertexElement("Position", GpuVertexElementFormat.Float3) }),
                },
                out _);

            // The padding survives, which is what an interleaved buffer with a reserved tail needs.
            Assert.Equal(32u, declared[0].Stride);
        }

        /// <summary>
        /// The step function and its rate, including the floor Metal needs. A per-vertex layout declares a rate of
        /// 0 and Metal rejects 0 on any layout, so both arms come out at least 1.
        /// </summary>
        [Fact]
        public void TheStepRateIsFlooredAtOneOnBothArms()
        {
            MetalVertexStream[] streams = MetalVertexPlan.Build(
                new List<GpuVertexLayoutDescription>
                {
                    new(new GpuVertexElement("Position", GpuVertexElementFormat.Float3)),
                    new(0, 1, new[] { new GpuVertexElement("Instance", GpuVertexElementFormat.Float4) }),
                    new(0, 4, new[] { new GpuVertexElement("EveryFourth", GpuVertexElementFormat.Float4) }),
                },
                out _);

            Assert.Equal(MTLVertexStepFunction.PerVertex, streams[0].StepFunction);
            Assert.Equal(1u, streams[0].StepRate);

            Assert.Equal(MTLVertexStepFunction.PerInstance, streams[1].StepFunction);
            Assert.Equal(1u, streams[1].StepRate);

            // A DIVISOR IS HONOURED HERE, unlike on Vulkan, where the core vertex input rate is two-valued and
            // VulkanVertexInput refuses anything above 1. Metal has a real stepRate field, so nothing is lost and
            // nothing has to be refused.
            Assert.Equal(MTLVertexStepFunction.PerInstance, streams[2].StepFunction);
            Assert.Equal(4u, streams[2].StepRate);
        }

        /// <summary>The fullscreen case: no layouts, no streams, no attributes, and no refusal.</summary>
        [Fact]
        public void NoVertexLayoutsIsTheFullscreenCaseAndIsLegal()
        {
            MetalVertexStream[] none = MetalVertexPlan.Build(null, out MetalVertexAttribute[] attributes);

            Assert.Empty(none);
            Assert.Empty(attributes);
            Assert.Empty(MetalVertexPlan.Build(new List<GpuVertexLayoutDescription>(), out _));
        }

        /// <summary>The format map is total over the seam's four members and its size map agrees with it.</summary>
        [Fact]
        public void EveryVertexFormatMapsAndItsSizeAgrees()
        {
            Assert.Equal(MTLVertexFormat.Float, MetalFormats.ToVertexFormat(GpuVertexElementFormat.Float1));
            Assert.Equal(MTLVertexFormat.Float2, MetalFormats.ToVertexFormat(GpuVertexElementFormat.Float2));
            Assert.Equal(MTLVertexFormat.Float3, MetalFormats.ToVertexFormat(GpuVertexElementFormat.Float3));
            Assert.Equal(MTLVertexFormat.Float4, MetalFormats.ToVertexFormat(GpuVertexElementFormat.Float4));

            Assert.Equal(4u, MetalFormats.VertexElementSize(GpuVertexElementFormat.Float1));
            Assert.Equal(8u, MetalFormats.VertexElementSize(GpuVertexElementFormat.Float2));
            Assert.Equal(12u, MetalFormats.VertexElementSize(GpuVertexElementFormat.Float3));
            Assert.Equal(16u, MetalFormats.VertexElementSize(GpuVertexElementFormat.Float4));

            // A member added to the seam is refused rather than guessed at, in both directions.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => MetalFormats.ToVertexFormat((GpuVertexElementFormat)99));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => MetalFormats.VertexElementSize((GpuVertexElementFormat)99));
        }

        // A table whose VERTEX stage reads one uniform buffer at the given index. Hand-built for the reason
        // MetalShaderIndexTableRefusalTests gives: no shipped program can reach the shapes below.
        // Since 18.0.0 an index cannot be handed to the table: MslIndexRemap assigns it by walking the layout in
        // ascending (set, binding) with a counter per argument table, so the way to reach index N is to declare N
        // uniform buffers in front of the one this stage reads.
        static MetalShaderIndexTable VertexTableAt(int index)
        {
            var kinds = new GpuResourceKind[index + 1];
            Array.Fill(kinds, GpuResourceKind.UniformBuffer);

            return MetalShaderIndexTable.Build(
                Layout(kinds),
                new[]
                {
                    new MetalStageResourceUse(MetalShaderStage.Vertex, new[] { new MslResourceRef(0, index) }),
                },
                "hand-built");
        }

        static GpuResourceLayoutDescription[] Layout(params GpuResourceKind[] kinds)
        {
            var elements = new GpuResourceLayoutElement[kinds.Length];
            for (int i = 0; i < kinds.Length; i++)
                elements[i] = new GpuResourceLayoutElement("e" + i, kinds[i], GpuShaderStages.Vertex);
            return [new GpuResourceLayoutDescription(elements)];
        }

    }
}
