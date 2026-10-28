using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    WaterColumnColorSource? _waterColumnColor;
    readonly WaterSettings _waterColumnScratch = new();
    int _waterColumnOptedInPlanes;

    /// <summary>Diagnostics for the pre-water colour source of the latest rendered frame. Begin preserves it.</summary>
    public WaterColumnDiagnostics WaterColumnLightDiagnostics { get; private set; }

    // Authoring checks and the opted-in count, with no device work. Runs at preparation and again at render.
    void ValidateWaterColumnInputs()
    {
        _waterColumnOptedInPlanes = 0;
        foreach (WaterPlane plane in WaterDemandPlanes())
        {
            WaterSettings effective = plane.Look?.ResolveInto(_waterColumnScratch, Post.Water) ?? Post.Water;
            // Unbound water retains late-bound authoring, including on planes culled before UBO packing.
            WaterColumnValidation.Validate(effective);
            if (effective.WaterColumnLight) _waterColumnOptedInPlanes++;
        }
    }

    // Opens no phase. The viewport form runs it inside its device.water.prepare root and the no-argument form first
    // runs it at render. Render repeats it on either form, a no-op unless late-bound targets changed since.
    void PrepareWaterColumnColor()
    {
        if (_waterColumnOptedInPlanes == 0 && _waterColumnColor is null) return;
        (_waterColumnColor ??= new WaterColumnColorSource(_gd, _retired)).Prepare(_res, _waterColumnOptedInPlanes > 0);
        _water.ColumnShadows.SetColor(_waterColumnColor.Texture, _waterColumnColor.Generation);
    }

    void DrawWaterWithColumnInputs(IGpuCommandList cl, Matrix4x4 vp, Matrix4x4 displayVp, Vector3 eye, ref float waterSyncMs)
    {
        // After sky and ground decals, before every water draw, including legacy planes sharing the queue.
        WaterColumnCopyPath path = _waterColumnColor?.Record(cl, _res, _waterColumnOptedInPlanes > 0)
            ?? WaterColumnCopyPath.None;
        if (_waterColumnColor is { } source) _water.ColumnShadows.SetColor(source.Texture, source.Generation);
        WaterColumnLightDiagnostics = new(_waterColumnOptedInPlanes, path != WaterColumnCopyPath.None, path);
        using (ProfilePass(cl, "water", executed: _waterPlanes.Count > 0 || _boundedWater.Count > 0))
            RenderWater(cl, vp, displayVp, eye, ref waterSyncMs);
    }

    Rendering.WaterRenderer CreateWaterRenderer(IGpuDevice device) => new(device,
        _res.ColorDepthFB.Outputs, _retired, _atmosphere.Bindings,
        (_model.ShadowMap.ShadowTexture, _model.ShadowMap.ShadowSampler, _model.SharedFrameUniforms));

    void BindWaterColumnShadows(Rendering.WaterRenderer water) => water.ColumnShadows.SetShadows(
        _model.ShadowMap.ShadowTexture, _model.ShadowMap.ShadowSampler, _model.SharedFrameUniforms);

    void InvalidateShadowDepthAndWaterBindings()
    {
        _shadowPassRendered = false;
        _water.InvalidateColumnShadowBindings();
    }
}
