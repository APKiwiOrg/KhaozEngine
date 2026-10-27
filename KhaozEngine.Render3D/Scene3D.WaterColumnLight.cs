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

    void PrepareWaterColumnInputs()
    {
        WaterColumnLightDiagnostics = default;
        _waterColumnOptedInPlanes = 0;
        foreach (WaterPlane plane in _waterPlanes)
        {
            WaterSettings effective = plane.Look?.ResolveInto(_waterColumnScratch, Post.Water) ?? Post.Water;
            // Unbound water retains late-bound authoring, including on planes culled before UBO packing.
            WaterColumnValidation.Validate(effective);
            if (effective.WaterColumnLight) _waterColumnOptedInPlanes++;
        }
        (_waterColumnColor ??= new WaterColumnColorSource(_gd, _retired)).Prepare(_res, _waterColumnOptedInPlanes > 0);
    }

    void DrawWaterWithColumnInputs(IGpuCommandList cl, Matrix4x4 vp, Matrix4x4 displayVp, Vector3 eye)
    {
        // After sky and ground decals, before every water draw, including legacy planes sharing the queue.
        WaterColumnCopyPath path = _waterColumnColor!.Record(cl, _res, _waterColumnOptedInPlanes > 0);
        WaterColumnLightDiagnostics = new(_waterColumnOptedInPlanes, path != WaterColumnCopyPath.None, path);
        using (ProfilePass(cl, "water", executed: _waterPlanes.Count > 0))
            if (_waterPlanes.Count > 0)
            {
                _water.Draw(cl, _res, RelativeWaterPlanes(), vp, displayVp,
                    Post.LightDirection, Post.LightColor, eye, Post.Water, Post.Sky, EffectTimeSeconds, _frameOrigin, Post.Atmosphere.Enabled);
                _frameStats.DrawCalls++;
            }
    }

    Rendering.WaterRenderer CreateWaterRenderer(IGpuDevice device)
    {
        var water = new Rendering.WaterRenderer(device, _res.ColorDepthFB.Outputs, _retired, _atmosphere.Bindings);
        BindWaterColumnShadows(water);
        return water;
    }

    void BindWaterColumnShadows(Rendering.WaterRenderer water) => water.ColumnShadows.SetShadows(
        _model.ShadowMap.ShadowTexture, _model.ShadowMap.ShadowSampler, _model.SharedFrameUniforms);

    void InvalidateShadowDepthAndWaterBindings()
    {
        _shadowPassRendered = false;
        _water.InvalidateColumnShadowBindings();
    }
}
