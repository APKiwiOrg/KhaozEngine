namespace KhaozEngine.Gpu;

/// <summary>Why GpuSwapchainCopy refused an operation.</summary>
public enum GpuSwapchainCopyRefusal
{
    /// <summary>Refuses a swapchain source without exactly one colour attachment.</summary>
    ColourAttachmentCount,
    /// <summary>Refuses a swapchain copy destination whose complete texture shape differs.</summary>
    DestinationShapeMismatch,
    /// <summary>Refuses a swapchain copy before the source swapchain is bound in this recording.</summary>
    SwapchainNotBound,
    /// <summary>Refuses a swapchain copy without a live copyable swapchain image.</summary>
    SwapchainNotCopyable,
}
