using System;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

/// <summary>Finite procedural displacement certification for bounded original-domain clipping. FFT displacement
/// needs sea-state inputs that WaterLook does not carry and cannot be certified by this contract.</summary>
public static class WaterSurfaceDisplacement
{
    const int RoundingGuardUlps = 32;

    /// <summary>Conservative horizontal reach over every supported component count and direction. Geometry-driving
    /// fields must be explicit so later edits to scene defaults cannot invalidate an already prepared domain.</summary>
    public static double MaximumHorizontal(WaterLook look)
    {
        ArgumentNullException.ThrowIfNull(look);
        if (look.WaveSource != WaterWaveSource.Procedural || look.SwellAmplitude is not { } amplitude ||
            look.SwellWavelength is not { } wavelength || look.SwellSteepness is not { } steepness)
            throw new NotSupportedException("Bounded water requires explicit procedural source, amplitude, wavelength and steepness.");
        if (!float.IsFinite(amplitude) || !float.IsFinite(wavelength) || !float.IsFinite(steepness))
            throw new ArgumentException("Water displacement parameters must be finite.", nameof(look));
        if (look.SwellDirectionDegrees is { } direction && !float.IsFinite(direction) ||
            look.SwellSpreadDegrees is { } spread && !float.IsFinite(spread) ||
            look.SwellSpeed is { } speed && !float.IsFinite(speed) || look.SwellSeed is { } seed && !float.IsFinite(seed))
            throw new ArgumentException("Explicit swell generation parameters must be finite.", nameof(look));
        if (!WaterSwellReach.Displaces(amplitude, wavelength)) return 0;
        float directionRadians = GerstnerWaves.DegreesToRadians(look.SwellDirectionDegrees ?? 0);
        float spreadRadians = GerstnerWaves.DegreesToRadians(look.SwellSpreadDegrees ?? 0);
        double largest = checked(Math.Abs((double)steepness) * wavelength) / (2 * Math.PI);
        Span<GerstnerWaves.Component> components = stackalloc GerstnerWaves.Component[GerstnerWaves.MaxComponents];
        // The shader clamps counts to 1..8. Validate every count with the effective generation parameters.
        // Zero steepness still has vertical waves, so invalid frequencies or phases also poison its positions.
        // Each orbital radius is abs(Q_i * A_i), independent of phase and direction.
        for (int count = 1; count <= GerstnerWaves.MaxComponents; count++)
        {
            GerstnerWaves.BuildComponents(amplitude, wavelength, directionRadians, spreadRadians, steepness,
                look.SwellSpeed ?? 0, look.SwellSeed ?? 0, count, components);
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                var component = components[i];
                float radius = MathF.Abs(component.Steepness * component.Amplitude);
                if (!float.IsFinite(component.DirX) || !float.IsFinite(component.DirZ) || !float.IsFinite(component.WaveNumber) ||
                    !float.IsFinite(component.AngularSpeed) || !float.IsFinite(component.Amplitude) ||
                    !float.IsFinite(component.Steepness) || !float.IsFinite(component.Phase) || !float.IsFinite(radius))
                    throw new ArgumentException("Water displacement cannot be certified in binary32.", nameof(look));
                sum = checked(sum + radius);
            }
            largest = Math.Max(largest, sum);
        }
        if (steepness == 0) return 0;
        float upper = (float)largest;
        if ((double)upper < largest) upper = MathF.BitIncrement(upper);
        // Outward binary32 allowance covers the bounded component generation and eight-term accumulation.
        // The separate clip epsilon accounts for local submission and domain-boundary comparisons.
        for (int i = 0; i < RoundingGuardUlps; i++) upper = MathF.BitIncrement(upper);
        if (!float.IsFinite(upper)) throw new ArgumentException("Water displacement exceeds its finite envelope.", nameof(look));
        return upper;
    }
}
