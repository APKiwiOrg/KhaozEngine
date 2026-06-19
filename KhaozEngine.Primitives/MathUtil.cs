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
}
