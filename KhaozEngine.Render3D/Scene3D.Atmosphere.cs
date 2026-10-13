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
            var model = Post.ResolveAtmosphereModel();
            var frame = AtmosphereRenderer.ResolveFrame(Post, model);
            var lighting = model.EvaluateLighting(Post.Atmosphere.HeightKm(eye.Y + _frameOrigin.Y), frame);
            // Refresh only the atmosphere-owned LightColor, AmbientColor, FillLightColor and Water.HorizonColor
            // so surface lighting and water agree with this altitude's LUT. Apply owns cycle geometry, while
            // callers can override directions and sky fields, including ExtraDiscs, after Apply.
            Post.LightColor = AtmosphereColor(lighting.Key);
            Post.AmbientColor = AtmosphereColor(lighting.Ambient);
            Post.FillLightColor = AtmosphereColor(lighting.Fill);
            Post.Water.HorizonColor = AtmosphereColor(lighting.Horizon);
        }
        _atmosphere.Record(cl, Post, eye, _frameOrigin, _currentFrameView.View,
            _currentFrameView.JitteredProjection, _res.Width, _res.Height);
    }

    static Color AtmosphereColor(Vector3 rgb) => new(rgb.X, rgb.Y, rgb.Z, 1f);
}
