using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    readonly AtmosphereRenderer _atmosphere;
    internal AtmosphereRenderer AtmosphereForTests => _atmosphere;

    /// <summary>Refreshes physical lighting at this render's absolute altitude, then records the LUT targets
    /// before shadow fitting or model uniforms read the lighting. Disabled frames never resolve CPU tables.</summary>
    internal void RecordAtmosphere(IGpuCommandList cl, Vector3 eye)
    {
        if (Post.Atmosphere.Enabled)
        {
            if (Post.AtmosphereCycle is { } cycle)
            {
                var applied = SunCycle.Apply(cycle, Post, eye + _frameOrigin);
                Post.Water.HorizonColor = applied.HorizonColor;
            }
            else
            {
                var model = Post.ResolveAtmosphereModel();
                var frame = AtmosphereRenderer.ResolveFrame(Post, model);
                var lighting = model.EvaluateLighting(Post.Atmosphere.HeightKm(eye.Y + _frameOrigin.Y), frame);
                Post.LightColor = AtmosphereColor(lighting.Key);
                Post.AmbientColor = AtmosphereColor(lighting.Ambient);
                Post.FillLightColor = AtmosphereColor(lighting.Fill);
                Post.Water.HorizonColor = AtmosphereColor(lighting.Horizon);
            }
        }
        _atmosphere.Record(cl, Post, eye, _frameOrigin, _currentFrameView.View,
            _currentFrameView.JitteredProjection, _res.Width, _res.Height);
    }

    static Color AtmosphereColor(Vector3 rgb) => new(rgb.X, rgb.Y, rgb.Z, 1f);
}
