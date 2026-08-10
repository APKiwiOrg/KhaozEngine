using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// ONE ENTRY OF THE <c>MTLVertexDescriptor</c>'s <c>layouts</c> ARRAY, resolved: which buffer index this slot
    /// is bound at, its stride, and how it advances.
    /// </summary>
    /// <param name="BufferIndex">The <c>[[buffer(n)]]</c> index, which is M-B2's top-pinned stream index. Row 14's
    /// <c>SetVertexBuffer</c> binds at the SAME number, and the two agreeing is the whole of what the scheme
    /// owes.</param>
    /// <param name="Stride">Bytes between consecutive elements: the declared stride when the layout gave one, and
    /// the packed sum of its element sizes when it did not.</param>
    /// <param name="StepFunction">Per vertex or per instance.</param>
    /// <param name="StepRate">Elements per step, raised to at least 1 because Metal rejects 0.</param>
    internal readonly record struct MetalVertexStream(
        uint BufferIndex, uint Stride, MTLVertexStepFunction StepFunction, uint StepRate);

    /// <summary>
    /// ONE ENTRY OF THE <c>MTLVertexDescriptor</c>'s <c>attributes</c> ARRAY, resolved: which shader attribute
    /// reads which buffer slot at which offset.
    /// </summary>
    /// <param name="AttributeIndex">The <c>[[attribute(n)]]</c> index, which counts across every slot rather than
    /// within one.</param>
    /// <param name="BufferIndex">The buffer index of the slot it is read from, already top-pinned.</param>
    /// <param name="Format">The component format, turned into an <c>MTLVertexFormat</c> at the descriptor.</param>
    /// <param name="OffsetBytes">Byte offset within its own slot.</param>
    internal readonly record struct MetalVertexAttribute(
        uint AttributeIndex, uint BufferIndex, GpuVertexElementFormat Format, uint OffsetBytes);

    /// <summary>
    /// THE VERTEX INPUT STATE, COMPUTED FROM THE SEAM'S OWN LAYOUTS AND M-B2's NUMBERING, WITH NO DEVICE
    /// ANYWHERE. What <c>MetalGraphicsPipeline</c> writes into an <c>MTLVertexDescriptor</c>, and what row 14
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580) reads to bind a stream at the matching index.
    ///
    /// <para><b>THE ATTRIBUTE INDEX COUNTS ACROSS ALL SLOTS, NOT WITHIN ONE.</b> Slot 1's first element continues
    /// where slot 0's last one left off, because SPIRV-Cross emits <c>[[attribute(n)]]</c> from the GLSL
    /// <c>location</c>, which is a single flat sequence over every vertex input the shader declares and knows
    /// nothing about which buffer an attribute arrives in. Both siblings apply the same rule from the same shared
    /// GLSL (<c>VulkanVertexInput</c>'s location, <c>D3D11InputLayoutPlan</c>'s semantic index), the incumbent's
    /// own <c>element</c> counter does the same thing, and getting it wrong reads the instance buffer's first
    /// attribute as the vertex buffer's second.</para>
    ///
    /// <para><b>OFFSETS ARE PACKED AND STRIDES ARE DECLARED-OR-COMPUTED.</b> The seam has no per-element offset,
    /// so an element sits immediately after the one before it in the same slot. A layout declaring a non-zero
    /// <see cref="GpuVertexLayoutDescription.Stride"/> keeps it, which is how an interleaved buffer with padding
    /// survives, and a zero stride is the sum of that slot's element sizes, which is what almost every shipped
    /// call site relies on.</para>
    ///
    /// <para><b>A STEP RATE ABOVE 1 IS HONOURED HERE, UNLIKE ON VULKAN.</b> Metal's
    /// <c>MTLVertexBufferLayoutDescriptor</c> has a real <c>stepRate</c>, so a divisor needs no extension and
    /// nothing is refused. <c>VulkanVertexInput.RequireExpressibleRate</c> refuses one because Vulkan's core
    /// vertex input rate is two-valued, which is a genuine difference between the two APIs rather than a
    /// divergence between the two backends. Every shipped instance stream declares a rate of exactly 1
    /// anyway.</para>
    ///
    /// <para><b>THE EMPTY CASE IS THE FULLSCREEN ONE AND IS LEGAL.</b> A pass that builds its geometry from
    /// <c>gl_VertexIndex</c> declares no vertex layouts at all, which yields no streams and no attributes. Six
    /// shipped renderers are in that shape.</para>
    ///
    /// <para><b>THE NUMBERING ITSELF IS NOT HERE, AND THAT IS THE POINT.</b> Every buffer index below comes from
    /// <see cref="MetalVertexStreamIndex"/>, the one type both readers of M-B2's numbering share: this plan
    /// writes the <c>MTLVertexDescriptor</c>'s layout index and row 13's flush writes the
    /// <c>setVertexBuffers:</c> bind index, and a device reports NOTHING when the two disagree. Two independent
    /// subtractions that happened to agree today is exactly the shape M-B2 exists to remove, so this file
    /// performs none of its own.</para>
    ///
    /// <para><b>AND M-B2's NO-COLLISION ASSERTION LIVES HERE rather than on that type</b>, because it is row 11's
    /// claim rather than the numbering's: it needs a PIPELINE's stream count and its binding table together,
    /// neither of which a shared index mapping has any business knowing.</para>
    /// </summary>
    internal static class MetalVertexPlan
    {
        /// <summary>
        /// Flatten <paramref name="layouts"/> into the streams and the attributes an
        /// <c>MTLVertexDescriptor</c> is written from.
        /// </summary>
        /// <param name="layouts">One layout per vertex buffer slot, in slot order. Null or empty is the fullscreen
        /// case.</param>
        /// <param name="attributes">The attributes, in attribute-index order.</param>
        /// <returns>The streams, indexed by the seam's vertex buffer SLOT, so row 14 can map a
        /// <c>SetVertexBuffer(slot, ...)</c> onto the buffer index this chose.</returns>
        /// <exception cref="ArgumentOutOfRangeException">An element declares a format with no Metal vertex
        /// format.</exception>
        internal static MetalVertexStream[] Build(IReadOnlyList<GpuVertexLayoutDescription>? layouts,
            out MetalVertexAttribute[] attributes)
        {
            if (layouts is null || layouts.Count == 0)
            {
                attributes = [];
                return [];
            }

            int total = 0;
            for (int slot = 0; slot < layouts.Count; slot++) total += ElementsOf(layouts[slot]).Length;

            var streams = new MetalVertexStream[layouts.Count];
            attributes = new MetalVertexAttribute[total];

            uint attribute = 0;
            int next = 0;
            for (int slot = 0; slot < layouts.Count; slot++)
            {
                GpuVertexLayoutDescription layout = layouts[slot];

                // THE GUARDED MAPPING, not a subtraction of this file's own. A slot past the bottom of the table
                // throws here rather than wrapping into a plausible-looking index, which is the arm
                // RequireNoCollision refuses first on the pipeline path and which nothing else may reach.
                uint bufferIndex = MetalVertexStreamIndex.ForSlot((uint)slot);

                GpuVertexElement[] declared = ElementsOf(layout);
                uint offset = 0;
                for (int i = 0; i < declared.Length; i++)
                {
                    attributes[next++] = new MetalVertexAttribute(
                        attribute++, bufferIndex, declared[i].Format, offset);

                    // The size map throws for a format Metal has no vertex format for, so the packing and the
                    // descriptor write cannot disagree about which formats exist.
                    offset += MetalFormats.VertexElementSize(declared[i].Format);
                }

                streams[slot] = new MetalVertexStream(
                    bufferIndex,
                    layout.Stride != 0 ? layout.Stride : offset,
                    layout.InstanceStepRate != 0 ? MTLVertexStepFunction.PerInstance
                        : MTLVertexStepFunction.PerVertex,

                    // The incumbent's own Math.Max(1, stepRate), reproduced: a per-vertex layout declares a rate
                    // of 0 and Metal rejects 0 on any layout, so the floor applies to both arms.
                    Math.Max(1u, layout.InstanceStepRate));
            }

            return streams;
        }

        /// <summary>
        /// M-B2'S NO-COLLISION ASSERTION, taken at pipeline creation against the indices the emission chose, and
        /// taken BEFORE <see cref="Build"/> runs.
        /// <para>
        /// TWO REFUSALS, AND THEY ARE DIFFERENT FAILURES. A pipeline declaring more vertex streams than one
        /// stage's buffer table holds is a caller asking for something the API cannot express at all, whatever
        /// shader it uses. A vertex-stage resource buffer landing in the top-pinned range is the combined-bindings
        /// case section 8.3 names, and it is a fact about the EMISSION meeting the declaration, which is why it
        /// reads as a shader validation failure and quotes both sides.
        /// </para>
        /// <para>
        /// THE COUNT ARM RUNS FIRST AND IT RUNS EARLY, which is an ordering obligation rather than a preference.
        /// <see cref="MetalVertexStreamIndex.ForSlot"/> refuses a slot the table cannot hold, so the plan cannot
        /// silently produce a wrapped index either way, but the message a caller gets should be this one: it
        /// names the pipeline, both numbers and the scheme, where the mapping's refusal only knows about a slot.
        /// So the caller asks this of the DECLARED layout count before the plan is built at all.
        /// </para>
        /// <para>
        /// IT IS CHECKED RATHER THAN ARGUED. The property is easy to believe from the arithmetic (resource
        /// buffers grow from 0, streams grow from 30 downward, and the shipped programs use a handful of each),
        /// and believing it is exactly how an inherited assumption survives the change that breaks it.
        /// <c>MetalVertexInputTests.NoShippedProgramsVertexStageReachesTheTopPinnedRange</c> takes the same
        /// measurement over every shipped program before any pipeline exists.
        /// </para>
        /// </summary>
        /// <param name="streamCount">How many vertex buffer slots the pipeline declares.</param>
        /// <param name="table">The program's binding table, whose vertex-stage buffer entries are the indices the
        /// emission chose.</param>
        /// <param name="label">A name for the pipeline, quoted in either message.</param>
        /// <exception cref="ArgumentOutOfRangeException">More vertex streams than the buffer table has
        /// entries.</exception>
        /// <exception cref="ShaderValidationException">A vertex-stage resource buffer landed in the range the
        /// streams occupy.</exception>
        internal static void RequireNoCollision(int streamCount, MetalShaderIndexTable table, string label)
        {
            ArgumentNullException.ThrowIfNull(table);

            if (streamCount < 0 || streamCount > MetalVertexStreamIndex.BufferTableSize)
            {
                throw new ArgumentOutOfRangeException(nameof(streamCount), streamCount,
                    $"{label}: a native Metal graphics pipeline declares "
                    + $"{streamCount.ToString(CultureInfo.InvariantCulture)} vertex buffer slots and one stage's "
                    + "buffer argument table has "
                    + $"{MetalVertexStreamIndex.BufferTableSize.ToString(CultureInfo.InvariantCulture)} "
                    + "entries, so they cannot all be bound whatever else the pipeline does. Vertex streams are "
                    + "pinned at the top of that space (M-B2), so slot 0 is buffer 30 and the count is what runs "
                    + "out.");
            }

            int lowest = MetalVertexStreamIndex.LowestIndexFor(streamCount);

            foreach ((MetalIndexTableKey key, MetalIndexTableEntry entry) in table.Entries())
            {
                if (key.Stage != MetalShaderStage.Vertex) continue;
                if (entry.Space != MetalIndexSpace.Buffer) continue;
                if (entry.Index < lowest) continue;

                throw new ShaderValidationException(
                    $"{label}: the emitted vertex function binds set "
                    + $"{key.Set.ToString(CultureInfo.InvariantCulture)} binding "
                    + $"{key.Binding.ToString(CultureInfo.InvariantCulture)} at "
                    + $"[[buffer({entry.Index.ToString(CultureInfo.InvariantCulture)})]], and this pipeline's "
                    + $"{streamCount.ToString(CultureInfo.InvariantCulture)} vertex streams occupy "
                    + $"{lowest.ToString(CultureInfo.InvariantCulture)} upward. Vertex streams take the top of "
                    + "the buffer space (M-B2) and resource buffers grow from 0, so the two collide only when a "
                    + "program's combined vertex-stage bindings exceed the "
                    + $"{MetalVertexStreamIndex.BufferTableSize.ToString(CultureInfo.InvariantCulture)} the table "
                    + "has. Binding both would put a vertex stream where a uniform is read.");
            }
        }

        /// <summary>
        /// The vertex-stage buffer indices one program's emission chose, in ascending order. The measurement
        /// behind <see cref="RequireNoCollision"/>, exposed so the corpus test can REPORT the headroom rather
        /// than only assert that there is some.
        /// </summary>
        internal static IReadOnlyList<int> VertexStageBufferIndices(MetalShaderIndexTable table)
        {
            ArgumentNullException.ThrowIfNull(table);

            var indices = new List<int>();
            foreach ((MetalIndexTableKey key, MetalIndexTableEntry entry) in table.Entries())
            {
                if (key.Stage == MetalShaderStage.Vertex && entry.Space == MetalIndexSpace.Buffer)
                    indices.Add(entry.Index);
            }

            indices.Sort();
            return indices;
        }

        static GpuVertexElement[] ElementsOf(in GpuVertexLayoutDescription layout) => layout.Elements ?? [];
    }
}
