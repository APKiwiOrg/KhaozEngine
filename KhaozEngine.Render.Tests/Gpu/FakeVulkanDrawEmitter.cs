using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>ONE <c>vkCmdBindVertexBuffers</c> AS THE DRIVER WOULD HAVE RECEIVED IT.</summary>
    /// <param name="CommandBuffer">The buffer it was recorded into.</param>
    /// <param name="FirstBinding">The run's first binding number.</param>
    /// <param name="Buffers">The <c>VkBuffer</c> per binding in the run.</param>
    /// <param name="Offsets">The byte offset per binding, positionally.</param>
    internal readonly record struct VulkanRecordedVertexBind(
        ulong CommandBuffer, uint FirstBinding, ulong[] Buffers, ulong[] Offsets);

    /// <summary>ONE <c>vkCmdBindIndexBuffer</c>.</summary>
    /// <param name="CommandBuffer">The buffer it was recorded into.</param>
    /// <param name="Buffer">The <c>VkBuffer</c>.</param>
    /// <param name="OffsetBytes">Where the first index lives.</param>
    /// <param name="SixteenBit">The element width.</param>
    internal readonly record struct VulkanRecordedIndexBind(
        ulong CommandBuffer, ulong Buffer, ulong OffsetBytes, bool SixteenBit);

    /// <summary>
    /// AN <see cref="IVulkanDrawEmitter"/> WITH NO DEVICE BEHIND IT, so the whole pre-command ORDER
    /// (<see cref="VulkanDrawRecorder"/>), the vertex run cutting and the dependent-dispatch barrier run under a
    /// plain <c>[Fact]</c> on a machine with no Vulkan loader. Work-breakdown row 15
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525).
    ///
    /// <para><b>IT REALLY FLUSHES THE DESCRIPTOR BINDS</b>, through <see cref="VulkanDrawBatch"/> over a
    /// <see cref="VulkanCapturingCmdSink"/>, exactly as the real emitter does over a
    /// <see cref="VulkanCmdSink"/>. A fake that only tallied the draw would leave the shipped flush-then-command
    /// pairing untested, which is the one ordering inside the emitter that can be wrong.</para>
    ///
    /// <para><b>IT KEEPS A TRACE AS WELL AS THE CALLS</b>, and the trace is the point for this seam: what row 15
    /// mostly owes is an ORDER (the image transitions, then the begin, then the geometry, then the binds, then the
    /// command), and an order is only assertable against one sequence that every participant appends to. The
    /// tracker's fake takes the same list, so a test can pin that the transitions come before the begin.</para>
    ///
    /// <para><b>A CLASS RATHER THAN A STRUCT</b>, like <see cref="FakeVulkanBarrierRecorder"/>: this seam is held
    /// as a field and consumed through the interface. The <see cref="IVkCmdSink"/> fakes are structs for the
    /// opposite reason.</para>
    /// </summary>
    internal sealed class FakeVulkanDrawEmitter : IVulkanDrawEmitter
    {
        readonly List<VulkanRecordedVertexBind> _vertexBinds = new();
        readonly List<VulkanRecordedIndexBind> _indexBinds = new();
        readonly List<VulkanRecordedBind> _descriptorBinds = new();
        readonly List<VulkanDrawCall> _draws = new();
        readonly List<VulkanIndexedDrawCall> _indexedDraws = new();
        readonly List<string> _trace;

        int _dispatches;
        int _dependencyBarriers;

        /// <param name="trace">A trace list to append to rather than own, so a test can assert the ORDER of these
        /// calls against another fake's. Its own list when null.</param>
        internal FakeVulkanDrawEmitter(List<string>? trace = null) => _trace = trace ?? new List<string>();

        /// <summary>Every <c>vkCmdBindVertexBuffers</c>, in order.</summary>
        internal IReadOnlyList<VulkanRecordedVertexBind> VertexBinds => _vertexBinds;

        /// <summary>Every <c>vkCmdBindIndexBuffer</c>, in order.</summary>
        internal IReadOnlyList<VulkanRecordedIndexBind> IndexBinds => _indexBinds;

        /// <summary>Every <c>vkCmdBindDescriptorSets</c> the flush inside a draw or dispatch produced.</summary>
        internal IReadOnlyList<VulkanRecordedBind> DescriptorBinds => _descriptorBinds;

        /// <summary>Every <c>vkCmdDraw</c>, in order.</summary>
        internal IReadOnlyList<VulkanDrawCall> Draws => _draws;

        /// <summary>Every <c>vkCmdDrawIndexed</c>, in order.</summary>
        internal IReadOnlyList<VulkanIndexedDrawCall> IndexedDraws => _indexedDraws;

        /// <summary>How many <c>vkCmdDispatch</c> calls were made.</summary>
        internal int DispatchCount => _dispatches;

        /// <summary>How many dependent-dispatch read-after-write barriers were emitted (V-C2). The number the
        /// whole hazard set exists to keep at exactly one per real dependency.</summary>
        internal int DependencyBarrierCount => _dependencyBarriers;

        /// <summary>Every call in order, as text, so a failing ordering assertion can print what actually
        /// happened.</summary>
        internal IReadOnlyList<string> Trace => _trace;

        /// <inheritdoc/>
        public void BindVertexBuffers(ulong commandBuffer, uint firstBinding, ReadOnlySpan<ulong> buffers,
            ReadOnlySpan<ulong> offsets)
        {
            _vertexBinds.Add(new VulkanRecordedVertexBind(
                commandBuffer, firstBinding, buffers.ToArray(), offsets.ToArray()));
            _trace.Add("BindVertexBuffers(first=" + firstBinding.ToString(CultureInfo.InvariantCulture)
                + ",count=" + buffers.Length.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <inheritdoc/>
        public void BindIndexBuffer(ulong commandBuffer, ulong buffer, ulong offsetBytes, bool sixteenBit)
        {
            _indexBinds.Add(new VulkanRecordedIndexBind(commandBuffer, buffer, offsetBytes, sixteenBit));
            _trace.Add("BindIndexBuffer(0x" + buffer.ToString("X", CultureInfo.InvariantCulture)
                + (sixteenBit ? ",u16)" : ",u32)"));
        }

        /// <inheritdoc/>
        public void Draw(ulong commandBuffer, VulkanBindRecords binds, in VulkanDrawCall call)
        {
            var sink = new VulkanCapturingCmdSink(_descriptorBinds);
            VulkanDrawBatch.Draw(ref sink, binds, in call);

            _draws.Add(call);
            _trace.Add("Draw(" + call.VertexCount.ToString(CultureInfo.InvariantCulture) + ","
                + call.InstanceCount.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <inheritdoc/>
        public void DrawIndexed(ulong commandBuffer, VulkanBindRecords binds, in VulkanIndexedDrawCall call)
        {
            var sink = new VulkanCapturingCmdSink(_descriptorBinds);
            VulkanDrawBatch.DrawIndexed(ref sink, binds, in call);

            _indexedDraws.Add(call);
            _trace.Add("DrawIndexed(" + call.IndexCount.ToString(CultureInfo.InvariantCulture) + ","
                + call.InstanceCount.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <inheritdoc/>
        public void Dispatch(ulong commandBuffer, VulkanBindRecords binds, uint groupCountX, uint groupCountY,
            uint groupCountZ)
        {
            var sink = new VulkanCapturingCmdSink(_descriptorBinds);
            VulkanDrawBatch.Dispatch(ref sink, binds, groupCountX, groupCountY, groupCountZ);

            _dispatches++;
            _trace.Add("Dispatch(" + groupCountX.ToString(CultureInfo.InvariantCulture) + ","
                + groupCountY.ToString(CultureInfo.InvariantCulture) + ","
                + groupCountZ.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <inheritdoc/>
        public void DependencyBarrier(ulong commandBuffer)
        {
            _dependencyBarriers++;
            _trace.Add("DependencyBarrier()");
        }
    }
}
