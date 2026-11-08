namespace KhaozEngine.Gpu;

/// <summary>Rules that refuse commands requiring a bound framebuffer or its depth attachment.</summary>
public enum GpuBoundFramebufferRefusal
{
    /// <summary>Refuses a framebuffer-dependent command when no framebuffer is bound.</summary>
    FramebufferNotBound,

    /// <summary>Refuses a depth clear when the bound framebuffer declares no depth attachment.</summary>
    DepthAttachmentMissing,
}
