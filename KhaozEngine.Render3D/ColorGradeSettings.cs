using System;

namespace KhaozEngine.Render3D;

/// <summary>Optional scene colour lookup after HDR tonemapping and before palette effects.</summary>
public sealed class ColorGradeSettings
{
    float _strength = 1f;

    /// <summary>A game-supplied display RGB cube. Null (the default) and identity bypass grading.</summary>
    public ColorGradeLut? Look { get; set; }

    /// <summary>Display RGB blend in [0, 1]. Zero bypasses grading. Defaults to one.</summary>
    public float Strength
    {
        get => _strength;
        set
        {
            if (!(value >= 0f && value <= 1f))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Colour grade strength must be in [0, 1].");
            _strength = value;
        }
    }
}
