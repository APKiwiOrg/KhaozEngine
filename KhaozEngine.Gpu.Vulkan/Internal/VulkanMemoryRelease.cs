using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal;

/// <summary>
/// Release queue writes before completion can retire their pooled allocation. The next owner can use a different
/// buffer reference or overwrite mapped memory on the host, so a resource range and its old usage are insufficient.
/// HOST_WRITE supplies the device-to-host domain operation before retirement observes completion. Coherent host
/// writes are subsequently published by queue submission. This does not replace the completion gate itself.
/// </summary>
internal static class VulkanMemoryRelease
{
    internal static unsafe void Record<TSink>(TSink sink) where TSink : struct, IVkCmdSink
    {
        var barrier = new MemoryBarrier2(
            sType: StructureType.MemoryBarrier2,
            srcStageMask: PipelineStageFlags2.AllCommandsBit,
            srcAccessMask: AccessFlags2.MemoryWriteBit,
            dstStageMask: PipelineStageFlags2.AllCommandsBit | PipelineStageFlags2.HostBit,
            dstAccessMask: AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit
                | AccessFlags2.HostReadBit | AccessFlags2.HostWriteBit);
        var dependency = new DependencyInfo(
            sType: StructureType.DependencyInfo,
            memoryBarrierCount: 1,
            pMemoryBarriers: &barrier);
        sink.PipelineBarrier(in dependency);
    }
}
