using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Immutable foliage motion coefficients. The default value preserves legacy blade motion.</summary>
public readonly record struct FoliageMotionProfile
{
    private static readonly Vector4 BladeCoefficients = new(1f, .65f, 1f, 0f);
    private readonly Vector4? _coefficients;

    /// <summary>Creates a profile from finite coefficients within their inclusive ranges.</summary>
    public FoliageMotionProfile(float BendScale, float MaxLean, float WindScale, float Shiver)
    {
        ValidateCoefficient(BendScale, 4f, nameof(BendScale));
        ValidateCoefficient(MaxLean, 1f, nameof(MaxLean));
        ValidateCoefficient(WindScale, 4f, nameof(WindScale));
        ValidateCoefficient(Shiver, .25f, nameof(Shiver));

        var coefficients = new Vector4(BendScale, MaxLean, WindScale, Shiver);
        _coefficients = coefficients == BladeCoefficients ? null : coefficients;
    }

    /// <summary>Interactor lean multiplier, from 0 to 4 inclusive.</summary>
    public float BendScale => ToVector4().X;

    /// <summary>Combined horizontal bend cap as a fraction of instance height, from 0 to 1 inclusive.</summary>
    public float MaxLean => ToVector4().Y;

    /// <summary>Wind sway multiplier, from 0 to 4 inclusive.</summary>
    public float WindScale => ToVector4().Z;

    /// <summary>Shiver amplitude at full agitation as a fraction of instance height, from 0 to 0.25 inclusive.</summary>
    public float Shiver => ToVector4().W;

    /// <summary>The legacy blade profile, also used by a default-initialized value.</summary>
    public static FoliageMotionProfile Blade => default;

    internal Vector4 ToVector4() => _coefficients ?? BladeCoefficients;

    private static void ValidateCoefficient(float value, float maximum, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0f || value > maximum)
            throw new ArgumentOutOfRangeException(parameterName, value,
                "Coefficient must be finite and within its allowed range.");
    }
}
