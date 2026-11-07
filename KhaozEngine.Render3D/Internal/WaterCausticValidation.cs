using System;

namespace KhaozEngine.Render3D.Internal;

internal static class WaterCausticValidation
{
    internal static void Validate(WaterSettings settings)
    {
        if (!settings.UnderwaterCaustics) return;

        if (!float.IsFinite(settings.CausticStrength) || settings.CausticStrength < 0f)
            throw new ArgumentException("CausticStrength must be finite and non-negative.",
                nameof(WaterSettings.CausticStrength));
    }
}
