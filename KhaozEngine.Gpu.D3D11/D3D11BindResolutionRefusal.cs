namespace KhaozEngine.Gpu.D3D11;

/// <summary>The rules of the D3D11BindResolution refusal family.</summary>
public enum D3D11BindResolutionRefusal
{
    /// <summary>Refuses a resource without the D3D11 bindable-view seam.</summary>
    BindableInterfaceMissing,
    /// <summary>Refuses a depth clear on a framebuffer without depth.</summary>
    DepthAttachmentMissing,
    /// <summary>Refuses a framebuffer without the D3D11 render-target seam.</summary>
    FramebufferInterfaceMissing,
    /// <summary>Refuses a framebuffer command before any framebuffer is bound.</summary>
    FramebufferNotBound,
    /// <summary>Refuses a buffer without a native D3D11 buffer handle.</summary>
    NativeBufferMissing,
    /// <summary>Refuses a null constant buffer carrying a nonzero window.</summary>
    NullBufferWindowNonzero,
    /// <summary>Refuses transposing a constant-buffer span into undersized scratch.</summary>
    TransposeScratchTooSmall,
    /// <summary>Refuses a resource without the native view needed by the register file.</summary>
    ViewMissing,
}
