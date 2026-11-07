using System;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Checks caustic authoring only for opted-in procedural bodies. FFT bodies ignore these settings.</summary>
internal static class WaterCausticValidation
{
    internal static void Validate(WaterSettings settings)
    {
        if (!settings.UnderwaterCaustics || settings.WaveSource != WaterWaveSource.Procedural) return;

        if (!float.IsFinite(settings.CausticStrength) || settings.CausticStrength < 0f)
            throw new ArgumentException("CausticStrength must be finite and non-negative.",
                nameof(WaterSettings.CausticStrength));
    }
}
