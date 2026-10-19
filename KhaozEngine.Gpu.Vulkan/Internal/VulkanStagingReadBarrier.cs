using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal;

/// <summary>Orders reads through a staging buffer whose allocation can reuse prior device-written memory.</summary>
internal static unsafe class VulkanStagingReadBarrier
{
    // A destination range barrier cannot cover accesses through a staging alias of another buffer.
    internal static MemoryBarrier2 BeforeCopy => new(
        sType: StructureType.MemoryBarrier2,
        srcStageMask: PipelineStageFlags2.AllCommandsBit,
        srcAccessMask: AccessFlags2.MemoryWriteBit,
        dstStageMask: PipelineStageFlags2.AllTransferBit,
        dstAccessMask: AccessFlags2.TransferReadBit);
}
