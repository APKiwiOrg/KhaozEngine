using System.Numerics;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Packs the column tail without changing the legacy water fields or the per-plane slot stride.</summary>
internal static class WaterColumnPacking
{
    internal static uint NoisePhase(long frameIndex, bool resolvesThisRender) =>
        resolvesThisRender ? (uint)(frameIndex % 64) : 0u;

    internal static void Pack(ref WaterRenderer.WaterUbo u, WaterSettings settings, uint noisePhase = 0)
    {
        if (!settings.WaterColumnLight)
        {
            u.ColumnExtinction = Vector4.Zero;
            u.ColumnScattering = Vector4.Zero;
            u.ColumnAlbedo = Vector4.Zero;
            return;
        }

        WaterColumnCoefficients coefficients = WaterColumnOptics.Derive(settings.WaterColor, settings.Clarity);
        u.ColumnExtinction = new Vector4(coefficients.Extinction, 1f);
        u.ColumnScattering = new Vector4(coefficients.Scattering, 0f);
        u.ColumnAlbedo = new Vector4(coefficients.Albedo, noisePhase);
    }
}
