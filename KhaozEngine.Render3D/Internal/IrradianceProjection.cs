using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Accumulates radiance samples before applying the clamped-cosine convolution once.</summary>
internal struct IrradianceProjection
{
    Vector3 _c0;
    Vector3 _c1;
    Vector3 _c2;
    Vector3 _c3;
    Vector3 _c4;
    Vector3 _c5;
    Vector3 _c6;
    Vector3 _c7;
    Vector3 _c8;

    public void Add(Vector3 unitDirection, Vector3 radiance, float solidAngle)
    {
        Span<float> basis = stackalloc float[IrradianceSH.CoefficientCount];
        IrradianceBasis.Evaluate(unitDirection, basis);
        var weightedRadiance = radiance * solidAngle;
        _c0 += weightedRadiance * basis[0];
        _c1 += weightedRadiance * basis[1];
        _c2 += weightedRadiance * basis[2];
        _c3 += weightedRadiance * basis[3];
        _c4 += weightedRadiance * basis[4];
        _c5 += weightedRadiance * basis[5];
        _c6 += weightedRadiance * basis[6];
        _c7 += weightedRadiance * basis[7];
        _c8 += weightedRadiance * basis[8];
    }

    public readonly IrradianceSH ToIrradiance()
    {
        const float band0 = MathF.PI;
        const float band1 = 2 * MathF.PI / 3;
        const float band2 = MathF.PI / 4;
        return new IrradianceSH([
            _c0 * band0,
            _c1 * band1,
            _c2 * band1,
            _c3 * band1,
            _c4 * band2,
            _c5 * band2,
            _c6 * band2,
            _c7 * band2,
            _c8 * band2,
        ]);
    }
}

/// <summary>The shared C# basis for projection and evaluation. The shader copy must match these constants.</summary>
internal static class IrradianceBasis
{
    internal const float Constant = 0.282095f;
    internal const float Linear = 0.488603f;
    const float QuadraticCross = 1.092548f;
    const float QuadraticZonal = 0.315392f;
    const float QuadraticDifference = 0.546274f;

    internal static void Evaluate(Vector3 unitDirection, Span<float> basis)
    {
        float x = unitDirection.X;
        float y = unitDirection.Y;
        float z = unitDirection.Z;
        basis[0] = Constant;
        basis[1] = Linear * y;
        basis[2] = Linear * z;
        basis[3] = Linear * x;
        basis[4] = QuadraticCross * x * z;
        basis[5] = QuadraticCross * y * z;
        basis[6] = QuadraticZonal * (3 * y * y - 1);
        basis[7] = QuadraticCross * x * y;
        basis[8] = QuadraticDifference * (x * x - z * z);
    }
}
