using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>One timestamp pool's driver calls. Handles cross this seam as integers.</summary>
    internal interface IVulkanTimestampApi
    {
        ulong CreatePool(uint capacity);
        void DestroyPool(ulong pool);
        void Reset(ulong commandBuffer, ulong pool, uint count);
        void Write(ulong commandBuffer, ulong pool, uint index);
        bool TryRead(ulong pool, uint count, Span<ulong> values, Span<ulong> availability);
    }
}
