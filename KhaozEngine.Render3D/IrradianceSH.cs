using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

/// <summary>RGB irradiance in the Y-up real L2 spherical harmonic basis. Coefficients already include
/// the clamped-cosine convolution, so evaluation only applies the basis and clamps negative channels.</summary>
public readonly struct IrradianceSH : IEquatable<IrradianceSH>
{
    /// <summary>The number of RGB coefficients in the L2 basis.</summary>
    public const int CoefficientCount = 9;

    readonly Vector3 _e0;
    readonly Vector3 _e1;
    readonly Vector3 _e2;
    readonly Vector3 _e3;
    readonly Vector3 _e4;
    readonly Vector3 _e5;
    readonly Vector3 _e6;
    readonly Vector3 _e7;
    readonly Vector3 _e8;

    /// <summary>Copies nine pre-convolved irradiance coefficients in basis order.</summary>
    /// <exception cref="ArgumentException">The span does not contain nine coefficients.</exception>
    public IrradianceSH(ReadOnlySpan<Vector3> coefficients)
    {
        if (coefficients.Length != CoefficientCount)
            throw new ArgumentException("Irradiance requires nine coefficients.", nameof(coefficients));

        _e0 = coefficients[0];
        _e1 = coefficients[1];
        _e2 = coefficients[2];
        _e3 = coefficients[3];
        _e4 = coefficients[4];
        _e5 = coefficients[5];
        _e6 = coefficients[6];
        _e7 = coefficients[7];
        _e8 = coefficients[8];
    }

    IrradianceSH(Vector3 e0, Vector3 e1, Vector3 e2, Vector3 e3, Vector3 e4,
        Vector3 e5, Vector3 e6, Vector3 e7, Vector3 e8)
    {
        _e0 = e0;
        _e1 = e1;
        _e2 = e2;
        _e3 = e3;
        _e4 = e4;
        _e5 = e5;
        _e6 = e6;
        _e7 = e7;
        _e8 = e8;
    }

    /// <summary>No irradiance. Equal to the default value.</summary>
    public static IrradianceSH Zero => default;

    /// <summary>Builds irradiance that is the same in every direction.</summary>
    public static IrradianceSH Uniform(Vector3 irradiance)
        => new(irradiance / IrradianceBasis.Constant, default, default, default, default,
            default, default, default, default);

    /// <summary>Builds uniform sky and ground hemispheres from their upward and downward irradiance.</summary>
    public static IrradianceSH SkyAndGround(Vector3 skyIrradiance, Vector3 groundIrradiance)
        => new(((skyIrradiance + groundIrradiance) / 2) / IrradianceBasis.Constant,
            ((skyIrradiance - groundIrradiance) / 2) / IrradianceBasis.Linear,
            default, default, default, default, default, default, default);

    /// <summary>Returns one pre-convolved RGB coefficient in basis order.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside [0, 8].</exception>
    public Vector3 Coefficient(int index) => index switch
    {
        0 => _e0,
        1 => _e1,
        2 => _e2,
        3 => _e3,
        4 => _e4,
        5 => _e5,
        6 => _e6,
        7 => _e7,
        8 => _e8,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "Coefficient index must be from 0 through 8."),
    };

    /// <summary>Returns irradiance at a unit surface normal, clamped to zero per channel.</summary>
    public Vector3 Evaluate(Vector3 unitNormal)
        => Vector3.Max(EvaluateUnclamped(unitNormal), Vector3.Zero);

    internal Vector3 EvaluateUnclamped(Vector3 unitNormal)
    {
        Span<float> basis = stackalloc float[CoefficientCount];
        IrradianceBasis.Evaluate(unitNormal, basis);
        return _e0 * basis[0] + _e1 * basis[1] + _e2 * basis[2]
            + _e3 * basis[3] + _e4 * basis[4] + _e5 * basis[5]
            + _e6 * basis[6] + _e7 * basis[7] + _e8 * basis[8];
    }

    /// <summary>Scales each coefficient by the corresponding RGB channel.</summary>
    public IrradianceSH Scale(Vector3 rgb)
        => new(_e0 * rgb, _e1 * rgb, _e2 * rgb, _e3 * rgb, _e4 * rgb,
            _e5 * rgb, _e6 * rgb, _e7 * rgb, _e8 * rgb);

    /// <summary>Adds irradiance coefficient by coefficient, before evaluation clamps negative values.</summary>
    public static IrradianceSH operator +(IrradianceSH a, IrradianceSH b)
        => new(a._e0 + b._e0, a._e1 + b._e1, a._e2 + b._e2, a._e3 + b._e3, a._e4 + b._e4,
            a._e5 + b._e5, a._e6 + b._e6, a._e7 + b._e7, a._e8 + b._e8);

    // Positive azimuth moves +X toward +Z, matching atan2(direction.Z, direction.X).
    internal IrradianceSH RotateY(float radians)
    {
        float cosine = MathF.Cos(radians);
        float sine = MathF.Sin(radians);
        float cosine2 = MathF.Cos(2 * radians);
        float sine2 = MathF.Sin(2 * radians);
        return new(_e0, _e1, _e3 * sine + _e2 * cosine, _e3 * cosine - _e2 * sine,
            _e8 * sine2 + _e4 * cosine2, _e7 * sine + _e5 * cosine, _e6,
            _e7 * cosine - _e5 * sine, _e8 * cosine2 - _e4 * sine2);
    }

    /// <inheritdoc />
    public bool Equals(IrradianceSH other)
        => _e0.Equals(other._e0) && _e1.Equals(other._e1) && _e2.Equals(other._e2)
            && _e3.Equals(other._e3) && _e4.Equals(other._e4) && _e5.Equals(other._e5)
            && _e6.Equals(other._e6) && _e7.Equals(other._e7) && _e8.Equals(other._e8);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is IrradianceSH other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_e0);
        hash.Add(_e1);
        hash.Add(_e2);
        hash.Add(_e3);
        hash.Add(_e4);
        hash.Add(_e5);
        hash.Add(_e6);
        hash.Add(_e7);
        hash.Add(_e8);
        return hash.ToHashCode();
    }

    /// <summary>Equal when all coefficients are equal.</summary>
    public static bool operator ==(IrradianceSH left, IrradianceSH right) => left.Equals(right);

    /// <summary>Different when any coefficient differs.</summary>
    public static bool operator !=(IrradianceSH left, IrradianceSH right) => !left.Equals(right);
}
