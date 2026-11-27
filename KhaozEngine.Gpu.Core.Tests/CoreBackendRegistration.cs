using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Core;

namespace KhaozEngine.Tests.GpuCore;

internal static class CoreBackendRegistration
{
    internal static void SeatAsVulkanNative()
        => GpuBackendProviders.Register(GpuBackendKind.VulkanNative, new CoreVulkanBackendProvider());
}
