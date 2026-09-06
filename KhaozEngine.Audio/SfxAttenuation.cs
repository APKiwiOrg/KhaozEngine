using System;

namespace KhaozEngine.Audio;

/// <summary>
/// Immutable inverse-distance attenuation curve for a positional SFX bus. Distances use the same world units
/// as listener and source positions.
/// </summary>
public readonly record struct SfxAttenuation
{
    /// <summary>The distance at which the source plays at its stated gain.</summary>
    public float ReferenceDistance { get; }

    /// <summary>How quickly gain falls beyond <see cref="ReferenceDistance"/>. Zero disables falloff.</summary>
    public float RolloffFactor { get; }

    /// <summary>The distance at which further attenuation is clamped.</summary>
    public float MaxDistance { get; }

    /// <summary>The historical engine curve: reference 1, rolloff 1, maximum 50.</summary>
    public static SfxAttenuation Default { get; } = new(1f, 1f, 50f);

    /// <summary>Creates a validated attenuation curve.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A value is non-finite, reference distance is not positive, rolloff is negative, or maximum distance is
    /// less than reference distance.
    /// </exception>
    public SfxAttenuation(float referenceDistance, float rolloffFactor, float maxDistance)
    {
        if (!float.IsFinite(referenceDistance) || referenceDistance <= 0f)
            throw new ArgumentOutOfRangeException(nameof(referenceDistance));
        if (!float.IsFinite(rolloffFactor) || rolloffFactor < 0f)
            throw new ArgumentOutOfRangeException(nameof(rolloffFactor));
        if (!float.IsFinite(maxDistance) || maxDistance < referenceDistance)
            throw new ArgumentOutOfRangeException(nameof(maxDistance));

        ReferenceDistance = referenceDistance;
        RolloffFactor = rolloffFactor;
        MaxDistance = maxDistance;
    }

    internal void Validate(string parameterName)
    {
        if (!float.IsFinite(ReferenceDistance) || ReferenceDistance <= 0f ||
            !float.IsFinite(RolloffFactor) || RolloffFactor < 0f ||
            !float.IsFinite(MaxDistance) || MaxDistance < ReferenceDistance)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The attenuation curve is invalid.");
        }
    }
}
