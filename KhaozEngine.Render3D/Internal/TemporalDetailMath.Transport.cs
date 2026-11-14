using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

internal static partial class TemporalDetailMath
{
    internal const float StillEpsilon = 1f / 16384f;
    internal const float StoreMax = 65504f;
    internal const int DistrustMask = 3;

    // Distrust is an OR of invalid/reset, offscreen, disoccluded and current/carried band marks.
    // Still motion and a slow lock are central-only. The rejection and mark halo is one display pixel.
    internal static bool UseFine(float motionPixels, bool locked, int distrust, int neighbourDistrust) =>
        motionPixels >= StillEpsilon && !(locked && motionPixels <= TemporalResolveTuning.ShimmerStillPixels)
        && ((distrust | neighbourDistrust) & DistrustMask) == 0;

    internal static float Retention(Vector3 resolved, Vector3 current, Vector3 mean, float weight, bool accepted)
    {
        if (!accepted || !Finite(resolved) || !Finite(current) || !Finite(mean)) return 0f;
        Vector3 delta = mean - current;
        float denominator = Vector3.Dot(delta, delta);
        float cap = Math.Clamp(1f - weight, 0f, 1f);
        float numerator = Vector3.Dot(resolved - current, delta);
        if (!float.IsFinite(denominator) || !float.IsFinite(numerator))
        {
            double hx = (double)mean.X - current.X, hy = (double)mean.Y - current.Y, hz = (double)mean.Z - current.Z;
            double divisor = hx * hx + hy * hy + hz * hz;
            double product = ((double)resolved.X - current.X) * hx + ((double)resolved.Y - current.Y) * hy
                + ((double)resolved.Z - current.Z) * hz;
            return divisor == 0 ? cap : (float)Math.Clamp(product / divisor, 0, cap);
        }
        return denominator == 0f ? cap
            : Math.Clamp(numerator / denominator, 0f, cap);
    }

    internal static float ChannelRetention(float r, Vector4 fetched, float scalar)
    {
        if (!Valid(r, fetched) || !(scalar > 0f)) return 0f;
        float mean = Mean(fetched);
        if (!float.IsFinite(mean)) return 0f;
        float limit = scalar;
        for (int i = 0; i < 4; i++)
        {
            float deviation = fetched[i] - mean;
            if (deviation < 0f) limit = MathF.Min(limit, r / -deviation);
            if (deviation > 0f) limit = MathF.Min(limit, (StoreMax - r) / deviation);
        }
        // Four interpolated float32 fetches can have a mean between representable floats. Also constrain the
        // fourth deviation implied by the first three. The stored fourth is their remainder, so this protects its
        // range even when rounding Hbar made the four independently subtracted deviations sum to a small residual.
        float remainderDeviation = -(((fetched.X - mean) + (fetched.Y - mean)) + (fetched.Z - mean));
        if (remainderDeviation < 0f) limit = MathF.Min(limit, r / -remainderDeviation);
        if (remainderDeviation > 0f) limit = MathF.Min(limit, (StoreMax - r) / remainderDeviation);
        float remainderChild = r + limit * remainderDeviation;
        if (remainderChild < 0f || remainderChild > StoreMax)
            return MathF.Max(0f, float.BitDecrement(limit));
        // A rounded quotient can lie just beyond the admissible interval. Pick the next representable
        // retention only when evaluating the actual four float32 expressions detects that case.
        for (int i = 0; i < 4; i++)
        {
            float child = r + limit * (fetched[i] - mean);
            if (child < 0f || child > StoreMax) return MathF.Max(0f, float.BitDecrement(limit));
        }
        return limit;
    }

    internal static Vector4 Transport(float r, Vector4 fetched, float scalar)
    {
        float k = ChannelRetention(r, fetched, scalar);
        if (k == 0f) return new Vector4(r);
        float mean = Mean(fetched);
        float a = Round(r + k * (fetched.X - mean));
        float b = Round(r + k * (fetched.Y - mean));
        float c = Round(r + k * (fetched.Z - mean));
        float d = Round(Math.Clamp(4f * r - ((a + b) + c), 0f, StoreMax));
        return new Vector4(a, b, c, d);
    }

    static float Mean(Vector4 fetched) => ((fetched.X + fetched.Y) + (fetched.Z + fetched.W)) * 0.25f;
    static float Round(float value) => (float)(Half)value;
    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    static bool Valid(float r, Vector4 fetched) => r >= 0f && r <= StoreMax
        && float.IsFinite(fetched.X) && float.IsFinite(fetched.Y)
        && float.IsFinite(fetched.Z) && float.IsFinite(fetched.W);
}
