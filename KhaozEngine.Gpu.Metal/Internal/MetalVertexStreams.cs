using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Gpu;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// DECISION M-B2: VERTEX STREAM BUFFERS TAKE THE TOP OF THE <c>[[buffer(n)]]</c> SPACE, 30 downward, and the
    /// assertion that nothing the emission chose can reach them. Section 8.3 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    ///
    /// <para><b>THE COLLISION THIS REMOVES IS THE ONLY REAL ONE IN METAL'S BINDING MODEL.</b> Vertex STREAM
    /// buffers and resource buffers share one <c>[[buffer(n)]]</c> space on the vertex stage, so a scheme is
    /// needed to keep them apart. <c>ResourceBindingModel</c> is the fork's answer and it makes one numbering
    /// depend on the other's COUNT: under <c>Improved</c>, which is the model this engine configures its Veldrid
    /// device with, a vertex stream lands at <c>NonVertexBufferCount + i</c>, summed over every resource layout.
    /// That count is the CPU's belief about where the resource buffers went, and section 2.2b's whole ruling is
    /// that the belief is not the authority: the emitted MSL is. So reproducing <c>Improved</c> would rest the
    /// stream numbering on exactly the quantity M-B1 removes.</para>
    ///
    /// <para><b>TOP-PINNING DEPENDS ON NOTHING.</b> Stream 0 is buffer 30, stream 1 is 29, and so on downward.
    /// Resource buffers grow from 0 upward wherever the emission put them, neither numbering reads the other, and
    /// <c>ResourceBindingModel</c> stops being a concept the engine has at all (M-B3).</para>
    ///
    /// <para><b>AND IT CHANGES NO PIXEL, WHICH IS WHAT MAKES IT FREE.</b> A vertex stream's buffer index is
    /// invisible to the emitted MSL, which reaches vertex attributes through <c>[[stage_in]]</c>. The index only
    /// has to agree between the <c>MTLVertexDescriptor</c>'s layout index and the <c>setVertexBuffer</c> index,
    /// and this backend owns both. That is why the 36 committed <c>metal</c> goldens are untouched by a change
    /// that renumbers every vertex stream in the engine.</para>
    ///
    /// <para><b>THE ASSERTION IS AGAINST THE TABLE AND NEVER AGAINST DECLARED VISIBILITY.</b>
    /// <see cref="RequireNoCollision"/> reads the vertex stage's own <c>[[buffer(n)]]</c> entries out of
    /// <see cref="MetalShaderIndexTable"/>, which are the indices the emission actually chose.
    /// <c>GpuResourceLayoutElement.Stages</c> is a DECLARATION of what should be visible and is not the authority
    /// on what a stage references: SPIRV-Cross omits an argument a stage does not use, so an element declared
    /// vertex-visible and never read has no entry and cannot collide with anything.</para>
    /// </summary>
    internal static class MetalVertexStreams
    {
        /// <summary>
        /// How many entries one stage's buffer argument table has. Metal guarantees at least 31 on every device
        /// this engine supports, so index 30 is the highest one a stream can take and 31 is the total.
        /// </summary>
        internal const int BufferTableSize = 31;

        /// <summary>The <c>[[buffer(n)]]</c> index vertex stream <paramref name="slot"/> is bound at: 30 for slot
        /// 0, 29 for slot 1, downward.</summary>
        /// <param name="slot">The seam's vertex buffer slot, which is the layout's position in
        /// <see cref="GpuPipelineDescription.VertexLayouts"/>.</param>
        internal static uint IndexOf(int slot) => (uint)(BufferTableSize - 1 - slot);

        /// <summary>
        /// The lowest buffer index the streams of a pipeline with <paramref name="streamCount"/> of them occupy.
        /// A resource buffer at or above this collides, and a program with no streams occupies nothing, which is
        /// why the answer for zero is the size of the table rather than 30.
        /// </summary>
        internal static uint LowestStreamIndex(int streamCount) => (uint)(BufferTableSize - streamCount);

        /// <summary>
        /// M-B2'S NO-COLLISION ASSERTION, taken at pipeline creation against the indices the emission chose.
        /// <para>
        /// TWO REFUSALS, AND THEY ARE DIFFERENT FAILURES. A pipeline declaring more vertex streams than the buffer
        /// table has is a caller asking for something the API cannot express at all, whatever shader it uses. A
        /// vertex-stage resource buffer landing in the top-pinned range is the combined-bindings case section 8.3
        /// names, and it is a fact about the EMISSION meeting the declaration, which is why it reads as a shader
        /// validation failure and quotes both sides.
        /// </para>
        /// <para>
        /// IT IS CHECKED RATHER THAN ARGUED. The property is easy to believe from the arithmetic (resource
        /// buffers grow from 0, streams grow from 30 downward, and the shipped programs use a handful of each),
        /// and believing it is exactly how an inherited assumption survives the change that breaks it. The
        /// device-free corpus row in <c>MetalVertexStreamTests</c> takes the same measurement over every shipped
        /// program before any pipeline exists.
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

            if (streamCount > BufferTableSize)
            {
                throw new ArgumentOutOfRangeException(nameof(streamCount), streamCount,
                    $"{label}: a native Metal graphics pipeline declares "
                    + $"{streamCount.ToString(CultureInfo.InvariantCulture)} vertex buffer slots and one stage's "
                    + $"buffer argument table has {BufferTableSize.ToString(CultureInfo.InvariantCulture)} "
                    + "entries, so they cannot all be bound whatever else the pipeline does. Vertex streams are "
                    + "pinned at the top of that space (M-B2), so slot 0 is buffer 30 and the count is what runs "
                    + "out.");
            }

            uint lowest = LowestStreamIndex(streamCount);

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
                    + $"{BufferTableSize.ToString(CultureInfo.InvariantCulture)} the table has. Binding both "
                    + "would put a vertex stream where a uniform is read.");
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
    }
}
