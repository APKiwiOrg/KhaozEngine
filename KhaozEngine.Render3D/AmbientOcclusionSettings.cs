using System;

namespace KhaozEngine.Render3D;

/// <summary>Short-range screen-space occlusion of opaque indirect diffuse light.</summary>
public sealed class AmbientOcclusionSettings
{
    float _radius = 1.05f;
    float _strength = 0.5f;

    /// <summary>Enable contact occlusion. Defaults to false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Search radius in metres. Defaults to 1.05 m. Must be positive and finite.</summary>
    public float RadiusMeters
    {
        get => _radius;
        set
        {
            if (!(value > 0f && float.IsFinite(value)))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The AO radius must be positive and finite.");
            _radius = value;
        }
    }

    /// <summary>Maximum local fraction of indirect diffuse removed, in [0, 1]. Defaults to 0.5. Zero bypasses AO.</summary>
    public float Strength
    {
        get => _strength;
        set
        {
            if (!(value >= 0f && value <= 1f))
                throw new ArgumentOutOfRangeException(nameof(value), value, "AO strength must be in [0, 1].");
            _strength = value;
        }
    }
}
