using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;
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
        using var cpu = ProfileCpu("cpu.atmosphere.lighting");
        if (Post.Atmosphere.Enabled)
        {
            var model = Post.ResolveAtmosphereModel();
            var frame = AtmosphereRenderer.ResolveFrame(Post, model);
            float heightKm = Post.Atmosphere.HeightKm(eye.Y + _frameOrigin.Y);
            var lighting = model.EvaluateLighting(heightKm, frame);
            // Refresh only the atmosphere-owned surface lighting and Water.HorizonColor
            // so surface lighting and water agree with this altitude's LUT. Apply owns cycle geometry, while
            // callers can override directions and sky fields, including ExtraDiscs, after Apply.
            Post.LightColor = AtmosphereColor(lighting.Key);
            AtmosphereSurfaceLighting.Write(Post, model, heightKm, frame,
                AtmosphereColor(lighting.Ambient), AtmosphereColor(lighting.Fill));
            Post.Water.HorizonColor = AtmosphereColor(lighting.Horizon);
        }
        _atmosphere.Record(cl, Post, eye, _frameOrigin, _currentFrameView.View,
            _currentFrameView.JitteredProjection, _res.Width, _res.Height);
    }

    static Color AtmosphereColor(Vector3 rgb) => new(rgb.X, rgb.Y, rgb.Z, 1f);
}
