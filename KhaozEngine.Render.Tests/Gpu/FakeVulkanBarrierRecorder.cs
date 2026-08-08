using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>ONE <c>vkCmdPipelineBarrier2</c> AS THE DRIVER WOULD HAVE RECEIVED IT: the buffer it went into and
    /// every image barrier it carried, in order.</summary>
    /// <param name="CommandBuffer">The buffer it was recorded into.</param>
    /// <param name="Barriers">The image barriers in the batch. Never empty: the tracker skips a call for no
    /// barriers rather than emitting one.</param>
    internal readonly record struct VulkanRecordedBarrierBatch(
        ulong CommandBuffer, ImageMemoryBarrier2[] Barriers);

    /// <summary>
    /// AN <see cref="IVulkanBarrierRecorder"/> WITH NO DEVICE BEHIND IT, so the whole layout tracker (V-F6 to
    /// V-F8) runs under a plain <c>[Fact]</c> on a machine with no Vulkan loader.
    /// <para>
    /// IT KEEPS THE BARRIERS AND NOT ONLY THE COUNTS, because both kinds of assertion are owed here. "One batched
    /// call per pass boundary rather than one per draw" is a count, and MV5's bound is stated in counts. "The old
    /// layout was the resting layout and not <c>UNDEFINED</c>" and "both stage masks are named" are arguments, and
    /// a barrier that synchronises nothing looks exactly like a correct one from a tally.
    /// </para>
    /// <para>
    /// A CLASS RATHER THAN A STRUCT, like <see cref="FakeVulkanRenderApi"/>: this seam is held as a field and
    /// consumed through the interface, deliberately, because nothing on it scales with draw count. The
    /// <see cref="IVkCmdSink"/> fakes are structs for the opposite reason.
    /// </para>
    /// </summary>
    internal sealed class FakeVulkanBarrierRecorder : IVulkanBarrierRecorder
    {
        readonly List<VulkanRecordedBarrierBatch> _batches = new();
        readonly List<string> _trace;

        /// <param name="trace">A trace list to append to rather than own, so a test can assert the ORDER of these
        /// calls against another fake's. Its own list when null.</param>
        internal FakeVulkanBarrierRecorder(List<string>? trace = null) => _trace = trace ?? new List<string>();

        /// <summary>Every <c>vkCmdPipelineBarrier2</c>, in order.</summary>
        internal IReadOnlyList<VulkanRecordedBarrierBatch> Batches => _batches;

        /// <summary>How many <c>vkCmdPipelineBarrier2</c> CALLS were made, which is half of what MV5 bounds.
        /// </summary>
        internal int CallCount => _batches.Count;

        /// <summary>How many BARRIERS were carried in total, which is the other half. A budget that froze only the
        /// call count would pass a recorder that put a barrier per draw into one batch.</summary>
        internal int BarrierCount
        {
            get
            {
                int total = 0;
                foreach (VulkanRecordedBarrierBatch batch in _batches) total += batch.Barriers.Length;
                return total;
            }
        }

        /// <summary>Every barrier from every batch, flattened, which is what a per-transition assertion reads.
        /// </summary>
        internal IReadOnlyList<ImageMemoryBarrier2> Barriers
        {
            get
            {
                var all = new List<ImageMemoryBarrier2>();
                foreach (VulkanRecordedBarrierBatch batch in _batches) all.AddRange(batch.Barriers);
                return all;
            }
        }

        /// <summary>Every call in order, as text, so a failing assertion can print what actually happened rather
        /// than only how many times.</summary>
        internal IReadOnlyList<string> Trace => _trace;

        /// <inheritdoc/>
        public void Emit(ulong commandBuffer, ReadOnlySpan<ImageMemoryBarrier2> barriers)
        {
            _batches.Add(new VulkanRecordedBarrierBatch(commandBuffer, barriers.ToArray()));

            var text = new System.Text.StringBuilder("PipelineBarrier2(");
            for (int i = 0; i < barriers.Length; i++)
            {
                if (i > 0) text.Append(',');
                text.Append("image 0x")
                    .Append(barriers[i].Image.Handle.ToString("X", CultureInfo.InvariantCulture))
                    .Append(' ')
                    .Append(barriers[i].OldLayout)
                    .Append("->")
                    .Append(barriers[i].NewLayout);
            }

            _trace.Add(text.Append(')').ToString());
        }
    }
}
