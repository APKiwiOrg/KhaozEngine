using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

internal static partial class TemporalDetailMath
{
    /// <summary>Stored motion below this many display pixels a frame is still and takes the coarse path, which is
    /// Display's own. The value is set by what the coarse path loses, not by any residual: one Catmull-Rom resample at
    /// a 128th of a pixel keeps 99.964 percent of a pattern that alternates every pixel, so nothing the fine path
    /// exists for lies below it. A still surface's stored motion is not always zero: its two clip positions can round
    /// apart, and the RG16F motion target stores whole least steps of 2^-24 UV. A step is a share of the display, so
    /// it grows in pixels with the display, and the count of steps grows with the scene's render-relative
    /// coordinates. This value holds 51 steps an axis at 2560 pixels a side, 16 at 8192 and eight at 16384. With
    /// camera-relative rendering in force a still view stores one step an axis at its render origin and up to 13
    /// with the scene 95 m from it (Metal, 1591 by 1027): a quarter of this value at the default budget's 2560 by
    /// 1440, and equal to it near 10,000 pixels a side. With the render origin opted out, a still view 1,024 m along
    /// each horizontal axis from the world origin stores 64 steps, above this value at 2560 by 1440. Where the
    /// stored steps reach it the still guarantee does not apply, as Display's own reprojection carries the same
    /// residual. 2^-14 would lie below a single step on any display wider or taller than 1024 pixels.</summary>
    internal const float StillEpsilon = 1f / 128f;
    internal const float StoreMax = 65504f;
    internal const int DistrustMask = 1;

    // Rejection covers invalid/reset, offscreen and disoccluded history. Followed/band marks remain observations.
    // Still motion and a slow lock are central-only. The rejection halo is one display pixel.
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
        // Products that leave float32 need colours far outside the store's range. The shader has no wider
        // arithmetic to fall back on and its result there is unspecified, so the mirror keeps nothing rather
        // than promise a value the shader does not compute.
        if (!float.IsFinite(denominator) || !float.IsFinite(numerator)) return 0f;
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
