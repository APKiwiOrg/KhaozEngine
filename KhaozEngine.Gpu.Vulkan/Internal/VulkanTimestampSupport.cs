using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>The selected queue's counter width and the physical device's timestamp period.</summary>
    internal readonly record struct VulkanTimestampSupport(int ValidBits, double TickPeriodNanoseconds)
    {
        internal bool IsSupported => ValidBits is >= 1 and <= 64
            && double.IsFinite(TickPeriodNanoseconds) && TickPeriodNanoseconds > 0d;
    }
}
