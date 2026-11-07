namespace KhaozEngine.Gpu.Vulkan;

/// <summary>Rules that refuse VulkanRingWait operations.</summary>
public enum VulkanRingWaitRefusal
{
    /// <summary>Refuses a Vulkan ring rotation while the caller holds the submit lock.</summary>
    SubmitLockHeld,
}
