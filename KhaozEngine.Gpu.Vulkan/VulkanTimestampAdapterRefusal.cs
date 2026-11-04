namespace KhaozEngine.Gpu.Vulkan;

/// <summary>Rules that refuse VulkanTimestampAdapter operations.</summary>
public enum VulkanTimestampAdapterRefusal
{
    /// <summary>Refuses a timestamp native operation that reports device loss.</summary>
    NativeDeviceLost,

    /// <summary>Refuses creating a second native query pool in one fixed adapter.</summary>
    PoolAlreadyCreated,
}
