using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
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
