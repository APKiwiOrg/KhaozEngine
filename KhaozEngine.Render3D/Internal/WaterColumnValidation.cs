using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D.Internal;

internal static class WaterColumnValidation
{
    internal static void Validate(WaterSettings settings)
    {
        if (!settings.WaterColumnLight) return;

        if (!float.IsFinite(settings.Clarity) || settings.Clarity <= 0f)
            throw new ArgumentException("Clarity must be finite and positive.", nameof(WaterSettings.Clarity));

        Color color = settings.WaterColor;
        if (!ValidChannel(color.R) || !ValidChannel(color.G) || !ValidChannel(color.B))
            throw new ArgumentException("WaterColor RGB channels must be finite and in [0, 1].",
                nameof(WaterSettings.WaterColor));
    }

    static bool ValidChannel(float channel) => float.IsFinite(channel) && channel >= 0f && channel <= 1f;
}
