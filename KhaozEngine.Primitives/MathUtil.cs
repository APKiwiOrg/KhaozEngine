namespace KhaozEngine.Primitives;

/// <summary>Pure scalar helpers shared across the engine. No allocation, no state.</summary>
public static class MathUtil
{
    /// <summary>Clamp to [0, 1].</summary>
    public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    /// <summary>Linear interpolation; <paramref name="t"/> is not clamped.</summary>
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Inverse of <see cref="Lerp"/>: where does <paramref name="v"/> sit in [a, b]? Degenerate (a == b) returns 0.</summary>
    public static float InverseLerp(float a, float b, float v) => a == b ? 0f : (v - a) / (b - a);

    /// <summary>Clamped Hermite smoothstep: 0 at x&lt;=a, 1 at x&gt;=b, smooth in between. Returns 0.5 at the midpoint.</summary>
    public static float SmoothStep(float a, float b, float x)
    {
        if (a == b) return x < a ? 0f : 1f;
        float t = System.Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
