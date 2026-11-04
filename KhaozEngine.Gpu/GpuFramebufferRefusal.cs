namespace KhaozEngine.Gpu;

/// <summary>Why GpuFramebuffer refused an operation.</summary>
public enum GpuFramebufferRefusal
{
    /// <summary>Refuses an attachment without the native texture or view needed for rendering.</summary>
    AttachmentNotRenderable,
    /// <summary>Refuses attachments that do not share one size and sample count.</summary>
    AttachmentShapeMismatch,
    /// <summary>Refuses an attachment with no required native render view.</summary>
    AttachmentViewMissing,
    /// <summary>Refuses a framebuffer with no colour or depth attachment.</summary>
    NoAttachments,
}
