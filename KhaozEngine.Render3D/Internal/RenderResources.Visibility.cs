using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal;

internal sealed partial class RenderResources
{
    /// <summary>Single-sample lit key-shadow visibility, null unless requested and supported.</summary>
    internal IGpuTexture? LitVisibilityTex { get; private set; }
    internal IGpuTexture? MsLitVisibilityTex { get; private set; }
    internal bool VisibilityAllocated => LitVisibilityTex is not null;

    void CreateVisibility(uint w, uint h, uint samples, bool requested)
    {
        if (!requested) return;
        LitVisibilityTex = Tex(w, h, ShadowVisibilityTargets.Format,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled);
        if (samples > 1)
            MsLitVisibilityTex = Tex(w, h, ShadowVisibilityTargets.Format, GpuTextureUsage.RenderTarget, 1, samples);
    }

    IGpuFramebuffer CreateModelFramebuffer(IGpuTexture color, IGpuTexture normal, IGpuTexture depth)
    {
        var attachments = new List<IGpuTexture> { color, normal, depth };
        if (MotionTex is not null) attachments.Add(MotionTex);
        if (LitVisibilityTex is not null) attachments.Add(MsLitVisibilityTex ?? LitVisibilityTex);
        return _gd.Factory.CreateFramebuffer(DepthStencil, attachments.ToArray());
    }

    void DisposeVisibility(GpuRetireQueue? retired)
    {
        ResourceRetirement.Free(MsLitVisibilityTex, retired);
        ResourceRetirement.Free(LitVisibilityTex, retired);
        MsLitVisibilityTex = LitVisibilityTex = null;
    }
}
