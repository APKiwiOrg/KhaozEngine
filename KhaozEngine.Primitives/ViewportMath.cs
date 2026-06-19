using System;

namespace KhaozEngine.Primitives;

/// <summary>
/// Uniform-scale helpers for fitting one rectangle inside another while preserving aspect ratio. Replaces
/// the open-coded <c>MathF.Min(w/W, h/H)</c> formula that was duplicated across windowing/camera/scene code.
/// </summary>
public static class ViewportMath
{
    /// <summary>Largest uniform scale that fits src entirely inside dst (letterbox). Aspect preserved.</summary>
    public static float Fit(float srcWidth, float srcHeight, float dstWidth, float dstHeight)
        => MathF.Min(dstWidth / srcWidth, dstHeight / srcHeight);

    /// <summary>Smallest uniform scale that covers dst entirely with src (crop). Aspect preserved.</summary>
    public static float Cover(float srcWidth, float srcHeight, float dstWidth, float dstHeight)
        => MathF.Max(dstWidth / srcWidth, dstHeight / srcHeight);
}
