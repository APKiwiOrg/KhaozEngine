using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Writes atmosphere surface lighting for the selected indirect diffuse mode.</summary>
internal static class AtmosphereSurfaceLighting
{
    internal static void Write(PixelPostProcessSettings post, AtmosphereModel model, float heightKm,
        in AtmosphereFrame frame, Color ambient, Color fill)
    {
        post.AmbientColor = ambient;
        if (post.IndirectDiffuse == IndirectDiffuseMode.Uniform)
        {
            post.FillLightColor = fill;
            post.AmbientIrradiance = IrradianceSH.Zero;
        }
        else
        {
            post.FillLightColor = new Color(0, 0, 0, fill.A);
            post.AmbientIrradiance = model.EvaluateIrradiance(heightKm, frame);
        }
    }
}
