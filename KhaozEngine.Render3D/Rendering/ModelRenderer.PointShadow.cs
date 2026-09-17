using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ModelRenderer
{
    /// <summary>
    /// The shared per-instance buffer this frame's grouped instances were uploaded into, for a pass that draws the
    /// SAME instances without a second upload. Null before the first <see cref="UploadInstances"/> of the scene's
    /// life, which is also the only state in which no caster can be drawn.
    /// <para>
    /// The key light's depth pass reaches the buffer by forwarding through <see cref="DrawShadowCasterRun"/>,
    /// because <see cref="ShadowMapRenderer"/> is owned here. The point-light pass is owned by
    /// <see cref="Scene3D"/> instead (it renders per light rather than per frame), so it takes the buffer as a
    /// draw argument the way <c>ShadowMapRenderer.DrawCasterRun</c> already does. Reading it per draw rather than
    /// caching it is what keeps a geometric buffer growth from leaving a pass drawing out of a retired buffer.
    /// </para>
    /// </summary>
    internal IGpuBuffer? InstanceBuffer => _instanceBuffer;
}
