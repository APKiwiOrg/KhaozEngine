using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ModelRenderer
{
    /// <summary>Bind + clear the model framebuffer once per frame, before drawing instances.</summary>
    public void BeginModelPass(IGpuCommandList cl, RenderResources res, PixelPostProcessSettings s, RenderPassProfiling? profiling = null)
    {
        using (profiling?.Pass(cl, "opaque.clear") ?? default)
        {
            cl.SetFramebuffer(res.ModelFB);
            // All three colour targets are cleared to the background, and alpha 0 marks "background" for the
            // starfield composite while the model writes alpha 1. That is what this pass wants on every backend.
            //
            // THE SECOND REASON THE THREE VALUES ARE EQUAL NAMES ONE IMPLEMENTATION: the VELDRID Metal backend
            // wrote every clear into colorAttachments[0], so attachments 1 and 2 were never cleared there at all
            // and they LOADED a freshly created private texture nothing had written. Equal values do not fix that,
            // they only make the one attachment that does get cleared carry the value the other two should have
            // had. The engine's own native Metal backend (GpuBackendKind.MetalNative) folds each clear into its
            // own attachment's loadAction, so all three really are cleared there, which is a deliberate rendering
            // change on the fleet's reference golden family rather than an invisible correction (decision M-A2 of
            // docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md). The values were kept equal while the Veldrid
            // Metal leg was still selectable, because this code could not tell which Metal it was on and had to
            // be correct on both. That leg went in 18.0.0 and the constraint went with it.
            var bg = s.BackgroundColor.WithAlpha(0f);
            cl.ClearColorTarget(0, bg);
            cl.ClearColorTarget(1, bg);
            cl.ClearColorTarget(2, bg);
        }
        // Background carries the sentinel, which marks no opaque geometry for every motion reader (MotionMath).
        using (profiling?.Pass(cl, "motion.clear", executed: res.MotionAllocated) ?? default)
            if (res.MotionAllocated) cl.ClearColorTarget(MotionMath.Attachment, MotionMath.SentinelColor);
        using (profiling?.Pass(cl, "opaque.visibility-clear", executed: res.VisibilityAllocated) ?? default)
            if (res.VisibilityAllocated) cl.ClearColorTarget((uint)ShadowVisibilityTargets.Attachment(res.MotionAllocated), Color.White);
        using (profiling?.Pass(cl, "opaque.depth-clear") ?? default) cl.ClearDepthStencil(1f);
    }

}
