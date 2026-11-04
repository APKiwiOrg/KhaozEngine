namespace KhaozEngine.Gpu.D3D11.Internal;

/// <summary>Classify missing render views without loading the native view types.</summary>
internal static class D3D11FramebufferAttachmentValidation
{
    /// <summary>Require the declared attachment's native view, preserving a valid view unchanged.</summary>
    internal static T RequireView<T>(T? view, string what, GpuTextureUsage usage, GpuTextureUsage actualUsage)
        where T : class
    {
        if (view is not null) return view;

        bool staging = (actualUsage & GpuTextureUsage.Staging) != 0;
        throw new GpuFramebufferException(staging
                ? GpuFramebufferRefusal.AttachmentNotRenderable
                : GpuFramebufferRefusal.AttachmentViewMissing,
            $"The {what} has no view to bind, because its texture was not created with "
            + $"GpuTextureUsage.{usage}. Views follow from the declared usage at creation, so a texture that "
            + "did not ask to be a target never got one.", null)
        {
            Attachment = what,
            ActualUsage = actualUsage,
            RequiredUsage = usage,
            IsStaging = staging,
            NativeHandleKind = usage == GpuTextureUsage.DepthStencil
                ? "ID3D11DepthStencilView" : "ID3D11RenderTargetView",
        };
    }
}
