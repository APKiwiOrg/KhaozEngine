using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Bounds stored-pose rounding for proposed capsule paths. These bounds authorize only
/// an enclosing query shape, never clearance or a position without the complete sweep.</summary>
internal static class MovementCapsuleRounding
{
    const double Skin = MovementQueryLease.CoverageSkinMetres;

    internal static bool TryEnclose(in MovementBodyQuery body, Vector3 delta, out MovementBodyQuery enclosed,
        out Vector3 endpoint, out double inflation)
    {
        enclosed = default;
        endpoint = body.Centre + delta;
        inflation = default;
        if (!body.IsValid || !MovementEnvironmentValidation.Finite(delta) ||
            !MovementEnvironmentValidation.Finite(endpoint)) return false;
        double rounding = Up(Up(SumError(body.Centre.X, delta.X, endpoint.X) +
            SumError(body.Centre.Y, delta.Y, endpoint.Y)) + SumError(body.Centre.Z, delta.Z, endpoint.Z));
        if (rounding > Skin) return false;
        if (rounding == 0 && body.HalfHeight <= (double)body.Radius * 1024)
        {
            double exactCore = (double)body.HalfHeight - body.Radius;
            float representedLength = 2f * (body.HalfHeight - body.Radius);
            if ((double)representedLength * 0.5 == exactCore)
            {
                // Both the endpoint sum and the CapsuleShape conversion are exact. Inflating this
                // already enclosing body would destroy a producer's exact tangency certificate.
                enclosed = body;
                return true;
            }
        }
        float radius = UpperFloat(Up(body.Radius + rounding));
        double cylinder = body.HalfHeight == body.Radius ? 0 : Up((double)body.HalfHeight - body.Radius);
        float halfHeight = UpperFloat(Up(radius + cylinder));
        // The public body converts half-height to the represented CapsuleShape cylinder length.
        // Verify the actual conversion, rather than assuming two upward sums suffice.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            float length = 2f * (halfHeight - radius);
            double actualCylinder = (double)length * 0.5;
            if (double.IsFinite(actualCylinder) && actualCylinder >= cylinder)
            {
                inflation = Up(Math.Max((double)radius - body.Radius,
                    radius + actualCylinder - body.HalfHeight));
                if (!float.IsFinite(radius) || !float.IsFinite(halfHeight) || inflation > Skin) return false;
                enclosed = new(body.Centre, radius, halfHeight, body.CurrentSpace, body.CurrentSupport);
                return true;
            }
            halfHeight = MathF.BitIncrement(halfHeight);
        }
        return false;
    }

    internal static bool TryImpact(Vector3 centre, Vector3 delta, double distance, double length,
        out Vector3 point, out double error)
    {
        point = default;
        error = default;
        if (!(length > 0) || !double.IsFinite(length) || !double.IsFinite(distance) || distance < 0 || distance > length)
            return false;
        double fraction = distance / length;
        point = new((float)(centre.X + delta.X * fraction), (float)(centre.Y + delta.Y * fraction),
            (float)(centre.Z + delta.Z * fraction));
        if (!MovementEnvironmentValidation.Finite(point)) return false;
        // Full binary32 spacings dominate final rounding. Thirty-two binary64 unit roundoffs
        // bound the non-underflowing float-input norm/divide/multiply/add expression's path error.
        error = Up(Up(Spacing(point.X) + Spacing(point.Y)) + Spacing(point.Z) +
            (length + Math.Abs(centre.X) + Math.Abs(centre.Y) + Math.Abs(centre.Z)) * 32d * 2.2204460492503131e-16);
        return double.IsFinite(error) && error <= Skin;
    }

    static double SumError(float a, float b, float stored)
    {
        if (a == 0 || b == 0) return 0;
        double sum = (double)a + b;
        // Two binary32 significands within 24 exponent steps fit exactly in binary64, including
        // cancellation. Only return zero when that exact sum is also the stored binary32 value.
        const double ratio = 16777216d;
        if (Math.Abs((double)a) <= Math.Abs((double)b) * ratio &&
            Math.Abs((double)b) <= Math.Abs((double)a) * ratio && sum == stored) return 0;
        double error = Math.Max(sum - Math.BitDecrement(sum), Math.BitIncrement(sum) - sum);
        return Up(Up(Math.Abs(sum - stored)) + error);
    }
    static double Spacing(float value) => Math.Max((double)MathF.BitIncrement(value) - value,
        (double)value - MathF.BitDecrement(value));
    static double Up(double value) => value == 0 ? 0 : Math.BitIncrement(value);
    static float UpperFloat(double value)
    {
        float result = (float)value;
        return result < value ? MathF.BitIncrement(result) : result;
    }
}
