using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>A value snapshot of foliage distance, density and cosmetic wind policy.</summary>
public readonly record struct FoliageRenderSettings
{
    /// <summary>Creates the default 40 metre draw policy with wind disabled.</summary>
    public FoliageRenderSettings() { }

    /// <summary>Maximum horizontal distance from the supplied focus, in world metres.</summary>
    public float DrawRadius { get; init; } = 40f;
    /// <summary>End of density thinning. Null follows DrawRadius, and values above DrawRadius are capped.</summary>
    public float? DensityRadius { get; init; }
    /// <summary>Width of the density transition and the outer height fade, in world metres.</summary>
    public float FadeBandWidth { get; init; } = 8f;
    /// <summary>Width of each thinning blade's height fade, bounded by the density transition.</summary>
    public float InstanceFadeBandWidth { get; init; } = 1f;
    /// <summary>Fraction of placements eligible nearby. Zero disables the submission.</summary>
    public float QualityDensity { get; init; } = 1f;
    /// <summary>Fraction retained beyond the density transition, capped by QualityDensity.</summary>
    public float DistantDensity { get; init; } = .35f;
    /// <summary>World XZ wind direction. Zero disables directional wind.</summary>
    public Vector2 WindDirection { get; init; } = new(1f, 0f);
    /// <summary>Wind displacement as a fraction from 0 through 1 of the full world-space blade height.</summary>
    public float WindStrength { get; init; } = 0f;
    /// <summary>Non-negative wind phase speed. Zero permits a stationary wind shape.</summary>
    public float WindSpeed { get; init; } = 1.8f;
    /// <summary>Non-negative world-space wind wave frequency.</summary>
    public float WindSpatialFrequency { get; init; } = .35f;

    /// <summary>Rejects non-finite values, negative distances or rates and fractions outside 0 through 1.</summary>
    public void Validate()
    {
        NonNegative(DrawRadius, nameof(DrawRadius));
        if (DensityRadius is float radius) NonNegative(radius, nameof(DensityRadius));
        NonNegative(FadeBandWidth, nameof(FadeBandWidth));
        NonNegative(InstanceFadeBandWidth, nameof(InstanceFadeBandWidth));
        Unit(QualityDensity, nameof(QualityDensity));
        Unit(DistantDensity, nameof(DistantDensity));
        Unit(WindStrength, nameof(WindStrength));
        NonNegative(WindSpeed, nameof(WindSpeed));
        NonNegative(WindSpatialFrequency, nameof(WindSpatialFrequency));
        if (!float.IsFinite(WindDirection.X) || !float.IsFinite(WindDirection.Y))
            throw new ArgumentException("Foliage wind direction must be finite.", nameof(WindDirection));
    }

    static void NonNegative(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0f)
            throw new ArgumentOutOfRangeException(name, "Foliage distances and rates must be finite and non-negative.");
    }

    static void Unit(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0f || value > 1f)
            throw new ArgumentOutOfRangeException(name, "Foliage fractions must be from 0 through 1.");
    }
}
