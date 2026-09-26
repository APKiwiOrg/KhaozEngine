using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// The fixed facts of the screen-space motion target (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 4) and the
/// arithmetic its writers and readers share. The target is the model framebuffer's fourth colour attachment while
/// temporal rendering is active. For the surface under each pixel it holds this frame's UV minus last frame's, from
/// unjittered clip positions, and a sentinel where no opaque geometry drew.
/// </summary>
internal static class MotionMath
{
    /// <summary>The motion target's colour attachment index in the model framebuffer.</summary>
    public const int Attachment = 3;

    /// <summary>Two half floats: U then V.</summary>
    public const GpuPixelFormat Format = GpuPixelFormat.R16G16Float;

    /// <summary>What both channels are cleared to: half max, far beyond any real motion.</summary>
    public const float Sentinel = 65504f;

    /// <summary>A channel whose magnitude passes this is the sentinel. Consumers test only this.</summary>
    public const float BackgroundThreshold = 60000f;

    /// <summary>The clear colour that writes <see cref="Sentinel"/> into both channels.</summary>
    public static Color SentinelColor => new(Sentinel, Sentinel, 0f, 0f);

    /// <summary>Whether a model-pass output description carries the motion attachment. Every renderer that draws into
    /// the model framebuffer builds its temporal variants for exactly this case.</summary>
    public static bool IsTemporal(in GpuOutputDescription outputs) => outputs.Colour.Length > Attachment;

    /// <summary>Whether a stored motion value is the background sentinel.</summary>
    public static bool IsBackground(Vector2 motion) => MathF.Abs(motion.X) > BackgroundThreshold;

    /// <summary>A last-frame clip w at or below this puts the point on or behind last frame's camera plane, where a
    /// perspective divide has no image position.</summary>
    public const float MinPreviousClipW = 1e-6f;

    /// <summary>The motion written for a point whose last-frame clip w is at or below <see cref="MinPreviousClipW"/>:
    /// two screens right and down. It reprojects off the screen, stays finite and below
    /// <see cref="BackgroundThreshold"/>, so a resolve rejects that pixel's history as it does for any off-screen
    /// previous position.</summary>
    public static Vector2 OffScreenMotion => new(2f, 2f);

    /// <summary>The largest motion written on either axis, in UV: two screens. The current UV lies in [0, 1], so a motion
    /// past 1 on an axis already puts the previous position off the screen, and one held at 2 keeps it there. No motion
    /// that stays on the screen is changed.</summary>
    public const float MaxMotionUv = 2f;

    /// <summary>The value a motion fragment writes for one surface point, <c>(ndcNow - ndcThen) * (0.5, -0.5)</c>: the
    /// difference of the two UVs, whose 0.5 offsets cancel, with V running down the image, clamped to
    /// <see cref="MaxMotionUv"/> on each axis. A last-frame w at or below <see cref="MinPreviousClipW"/> writes
    /// <see cref="OffScreenMotion"/> instead, because dividing by it would overflow RG16F to an infinity, or give NaN for
    /// 0/0, and every reader takes a value past the threshold for sky. The clamp covers a w just past that guard, where
    /// the divide is finite but can still pass the threshold or half precision. Clip x and y are finite and the only
    /// divisor below the guard is guarded, so the clamp never sees NaN.</summary>
    public static Vector2 UvMotion(Vector4 currentClip, Vector4 previousClip) =>
        previousClip.W <= MinPreviousClipW
            ? OffScreenMotion
            : Vector2.Clamp((new Vector2(currentClip.X, currentClip.Y) / currentClip.W
                - new Vector2(previousClip.X, previousClip.Y) / previousClip.W) * new Vector2(0.5f, -0.5f),
                new Vector2(-MaxMotionUv), new Vector2(MaxMotionUv));
}
