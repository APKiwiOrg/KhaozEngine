using System;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// ONE BATCH OF IMAGE BARRIERS AS ONE <c>vkCmdPipelineBarrier2</c>: build the <c>VkDependencyInfo</c> around
    /// the span and make the single sink call. The whole body of every <see cref="IVulkanBarrierRecorder"/> there
    /// is, so the two implementations differ ONLY in which <see cref="IVkCmdSink"/> they drive.
    ///
    /// <para><b>A STATIC OVER A GENERIC SINK, WHICH IS EXACTLY <see cref="VulkanBufferUpload"/>'s SHAPE AND FOR
    /// THE SAME REASON.</b> <see cref="IVkCmdSink"/> is consumed through a <c>where TSink : struct</c> constraint
    /// so the JIT monomorphizes it and boxes nothing (V-T2), and a type that STORED the sink in a field of the
    /// interface type would box it. So the sink is a PARAMETER here and the recorder that owns a real one calls in
    /// with its own, which is the same split the staged upload's buffer barrier takes.</para>
    ///
    /// <para><b>THAT SPLIT IS WHAT MAKES THE TRACKER'S BARRIERS COUNTABLE.</b> Both recorders reach the driver
    /// through this one function, so driving the tracker with <see cref="VulkanCountingBarrierRecorder"/> puts its
    /// image barriers into the SAME <see cref="VulkanCmdCallCounts"/> the binds and the staged upload's buffer
    /// barrier land in. An earlier shape built a concrete <see cref="VulkanCmdSink"/> inside the real recorder,
    /// which left <see cref="VulkanCmdCallCounts.BarrierCalls"/> structurally unable to see an image barrier at
    /// all, and would have made V-T2's per-draw barrier budget pass vacuously forever.</para>
    ///
    /// <para><b>NO DECISION OF ANY KIND LIVES HERE</b>, which is the emptiness <see cref="VulkanCmdSink"/> is
    /// built on: which layout an image is in, whether a barrier is needed and what its masks are all live above
    /// this line in device-free types.</para>
    /// </summary>
    internal static class VulkanBarrierBatch
    {
        /// <summary>
        /// ONE <c>vkCmdPipelineBarrier2</c> CARRYING EVERY BARRIER IN <paramref name="barriers"/>, or no call at
        /// all for an empty span, because a barrier call for no barriers is a native call bought for nothing.
        /// </summary>
        /// <typeparam name="TSink">The command sink, monomorphized at the call site.</typeparam>
        /// <param name="sink">Where the call is recorded.</param>
        /// <param name="barriers">The image barriers, already built with both stage masks and both access masks
        /// named (V-F6).</param>
        internal static unsafe void Emit<TSink>(TSink sink, ReadOnlySpan<ImageMemoryBarrier2> barriers)
            where TSink : struct, IVkCmdSink
        {
            if (barriers.Length == 0) return;

            // A VkDependencyInfo carries raw pointer arrays as a matter of ABI, which is why this package is
            // unsafe by construction (V-P1's note) rather than by choice.
            fixed (ImageMemoryBarrier2* pBarriers = barriers)
            {
                var dependency = new DependencyInfo(
                    sType: StructureType.DependencyInfo,
                    imageMemoryBarrierCount: (uint)barriers.Length,
                    pImageMemoryBarriers: pBarriers);

                sink.PipelineBarrier(in dependency);
            }
        }
    }
}
