using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// WHAT A RECORDING ASKED THE DRIVER FOR, IN THE THREE CLASSES THAT SCALE WITH DRAW COUNT: the tallies
    /// decision V-T2's budget is frozen over, plus a trace for the failure message.
    /// <para>
    /// A CLASS, so the readonly struct sink that writes into it stays a readonly struct. Same split
    /// <c>D3D11EmitterCallLog</c> takes, for the same reason: the sink is copied into whatever recorder drives it,
    /// so its state has to sit behind a reference or two copies would tally two different totals.
    /// </para>
    /// <para>
    /// BOTH BARRIER NUMBERS ARE KEPT, and that is not redundancy. A budget that froze only the CALL count would
    /// pass a recorder that emitted one <c>vkCmdPipelineBarrier2</c> per draw carrying one barrier, and a budget
    /// that froze only the barrier count would pass one that batched a thousand into a single call at the wrong
    /// point in the frame. The invariant is "no pipeline barriers on the per-draw path", which is a statement
    /// about both.
    /// </para>
    /// <para>
    /// THE DESCRIPTOR TALLIES COUNT SETS AND OFFSETS AS WELL AS CALLS, because the whole Vulkan argument for the
    /// bind model is that a full activation of the engine's four-set shapes collapses to ONE call carrying four
    /// sets, and an offsets-only rebind to one call carrying one. A call count alone cannot tell those two apart
    /// from four calls carrying one set each.
    /// </para>
    /// </summary>
    internal sealed class VulkanCmdCallCounts
    {
        readonly List<string> _trace = new();

        /// <summary><c>vkCmdBindDescriptorSets</c> calls.</summary>
        internal int BindDescriptorSetCalls { get; private set; }

        /// <summary>Sets named across every <see cref="BindDescriptorSetCalls"/>. Four in one call is the shape
        /// the descriptor model exists to produce.</summary>
        internal int DescriptorSetsBound { get; private set; }

        /// <summary>Dynamic offsets passed across every bind. It must equal the sum of the bound sets' dynamic
        /// descriptor counts, and the incumbent's own defect is failing to reset this between two batches inside
        /// one flush.</summary>
        internal int DynamicOffsetsPassed { get; private set; }

        /// <summary><c>vkCmdDraw</c> plus <c>vkCmdDrawIndexed</c> calls.</summary>
        internal int DrawCalls { get; private set; }

        /// <summary><c>vkCmdDispatch</c> calls.</summary>
        internal int DispatchCalls { get; private set; }

        /// <summary><c>vkCmdPipelineBarrier2</c> calls.</summary>
        internal int BarrierCalls { get; private set; }

        /// <summary>Individual memory, buffer and image barriers summed across every
        /// <see cref="BarrierCalls"/>.</summary>
        internal int BarriersEmitted { get; private set; }

        /// <summary>Every call in order, as text, so a failing budget assertion can print what actually happened
        /// rather than only how many times.</summary>
        internal IReadOnlyList<string> Trace => _trace;

        internal void NoteBind(uint firstSet, int sets, int dynamicOffsets)
        {
            BindDescriptorSetCalls++;
            DescriptorSetsBound += sets;
            DynamicOffsetsPassed += dynamicOffsets;
            Add($"BindDescriptorSets(first={firstSet},sets={sets},offsets={dynamicOffsets})");
        }

        internal void NoteDraw(string name, uint primaryCount, uint instanceCount)
        {
            DrawCalls++;
            Add($"{name}({primaryCount},{instanceCount})");
        }

        internal void NoteDispatch(uint x, uint y, uint z)
        {
            DispatchCalls++;
            Add($"Dispatch({x},{y},{z})");
        }

        internal void NoteBarrier(int barriers)
        {
            BarrierCalls++;
            BarriersEmitted += barriers;
            Add($"PipelineBarrier2({barriers})");
        }

        void Add(string entry) => _trace.Add(entry);
    }

    /// <summary>
    /// AN <see cref="IVkCmdSink"/> WITH NO DEVICE BEHIND IT: every call is tallied into a
    /// <see cref="VulkanCmdCallCounts"/> and nothing else happens. The vehicle for decision V-T2's device-free
    /// native-call budget, which runs under a plain <c>dotnet test</c> on macOS and Linux rather than as a
    /// <c>[GpuFact]</c> gated on a machine with a Vulkan driver.
    /// <para>
    /// WHAT IT MEASURES IS THE SHIPPED FAN-OUT, not a copy of it, because the seam it implements is the only line
    /// between the recorder and the driver for these three call classes. Everything that decides WHICH calls to
    /// make is above the seam and is driven unchanged: the dirty tracking, the run cutting, the positional
    /// dynamic-offset composition, the pipeline-compatibility invalidation. What a budget over this sink cannot
    /// see is anything that is not a sink call, which is deliberate and is why V-D2's descriptor-pool
    /// unreachability is a structural assertion rather than a count.
    /// </para>
    /// <para>
    /// THE BUDGET TEST ITSELF IS ROW 11's (https://github.com/APKiwiOrg/KhaozEngine/issues/521), because the
    /// numbers it freezes are produced by the bind flush that row builds. This row lands the seam and this sink so
    /// that the flush is written against a countable line from its first commit rather than retrofitted onto one.
    /// </para>
    /// <para>
    /// A READONLY STRUCT over one class reference, so the JIT monomorphizes it like the real sink and a copy of it
    /// still writes to the same tallies.
    /// </para>
    /// </summary>
    internal readonly struct VulkanCountingCmdSink : IVkCmdSink
    {
        readonly VulkanCmdCallCounts _counts;

        /// <param name="counts">The tallies to write into.</param>
        internal VulkanCountingCmdSink(VulkanCmdCallCounts counts)
        {
            ArgumentNullException.ThrowIfNull(counts);

            _counts = counts;
        }

        /// <summary>The tallies this sink writes into.</summary>
        internal VulkanCmdCallCounts Counts => _counts;

        /// <inheritdoc/>
        public void BindDescriptorSets(PipelineBindPoint bindPoint, PipelineLayout layout, uint firstSet,
            ReadOnlySpan<DescriptorSet> sets, ReadOnlySpan<uint> dynamicOffsets)
            => _counts.NoteBind(firstSet, sets.Length, dynamicOffsets.Length);

        /// <inheritdoc/>
        public void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
            => _counts.NoteDraw("Draw", vertexCount, instanceCount);

        /// <inheritdoc/>
        public void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset,
            uint firstInstance)
            => _counts.NoteDraw("DrawIndexed", indexCount, instanceCount);

        /// <inheritdoc/>
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
            => _counts.NoteDispatch(groupCountX, groupCountY, groupCountZ);

        /// <inheritdoc/>
        public void PipelineBarrier(in DependencyInfo dependency)
            => _counts.NoteBarrier(checked((int)(dependency.MemoryBarrierCount
                + dependency.BufferMemoryBarrierCount + dependency.ImageMemoryBarrierCount)));
    }
}
