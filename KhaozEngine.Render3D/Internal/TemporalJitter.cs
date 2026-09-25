using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// The sub-pixel camera jitter temporal rendering needs: which offset each frame takes and how it enters a
/// projection. Pure arithmetic, no GPU. See docs/design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md, section 1.
/// <para>
/// The offsets follow the Halton (2, 3) low-discrepancy sequence, shifted into the half-open range of minus half to
/// plus half an internal pixel. The sequence runs for <see cref="PhaseCount"/> frames and repeats: 8 at native
/// resolution, more when the display is larger than the internal target, so upscaled output pixels are covered evenly.
/// </para>
/// <para>
/// The offset enters as a clip-space translation, <c>P' = P * J</c> with <c>J</c> the identity plus <c>M41 = jx</c>
/// and <c>M42 = jy</c>. That adds <c>w * jx</c> to clip x and <c>w * jy</c> to clip y, so after the perspective divide
/// every point moves by the same NDC offset under a perspective and an orthographic projection alike. The offset in
/// pixels <c>(px, py)</c> runs x right and y down, the engine's pixel convention, so <c>jx = 2 * px / width</c> and
/// <c>jy = -2 * py / height</c>. <c>GpuClip.Correct</c> still applies at upload, after this.
/// </para>
/// </summary>
internal static class TemporalJitter
{
    /// <summary>The sequence length at native resolution.</summary>
    public const int NativePhaseCount = 8;

    /// <summary>The largest display over internal ratio per axis <see cref="PhaseCount"/> honours. A larger ratio reads
    /// as this one.</summary>
    public const float MaxDisplayOverInternalRatio = 4f;

    /// <summary>The longest sequence <see cref="PhaseCount"/> returns, 8 times 4 times 4, reached at a display
    /// <see cref="MaxDisplayOverInternalRatio"/> times the internal size per axis. Round 2's most aggressive preset,
    /// UltraPerformance, is three, which gives 72.</summary>
    public const int MaxPhaseCount = 128;

    /// <summary>How many frames the sequence runs before it repeats: 8 when the display is no larger than the internal
    /// target, else <c>ceil(8 * r * r)</c> for a display <paramref name="displayOverInternalRatio"/> times the internal
    /// size per axis. A ratio that is not a number reads as native, and a ratio above 4 reads as 4.</summary>
    public static int PhaseCount(float displayOverInternalRatio)
    {
        if (!(displayOverInternalRatio > 1f)) return NativePhaseCount;
        float r = MathF.Min(displayOverInternalRatio, MaxDisplayOverInternalRatio);
        return Math.Min(MaxPhaseCount, (int)MathF.Ceiling(NativePhaseCount * r * r));
    }

    /// <summary>Where <paramref name="frameIndex"/> falls in a sequence of <paramref name="phaseCount"/> frames, always
    /// in <c>[0, phaseCount)</c>, negative indices included.</summary>
    public static int Phase(long frameIndex, int phaseCount)
    {
        if (phaseCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(phaseCount), phaseCount, "A jitter sequence needs at least one phase.");
        long phase = frameIndex % phaseCount;
        return (int)(phase < 0 ? phase + phaseCount : phase);
    }

    /// <summary>The jitter for <paramref name="frameIndex"/> in internal pixels, x right and y down, each in
    /// <c>[-0.5, 0.5)</c>: Halton (2, 3) at index <c>phase + 1</c>, minus one half. Index zero is skipped because it is
    /// the unjittered centre in both bases.</summary>
    public static Vector2 Offset(long frameIndex, int phaseCount)
    {
        long index = Phase(frameIndex, phaseCount) + 1L;
        return new Vector2(Halton(index, 2) - 0.5f, Halton(index, 3) - 0.5f);
    }

    /// <summary>The radical inverse of <paramref name="index"/> in base <paramref name="radix"/>, in <c>[0, 1)</c>.</summary>
    public static float Halton(long index, int radix)
    {
        float result = 0f;
        float fraction = 1f;
        for (long i = index; i > 0; i /= radix)
        {
            fraction /= radix;
            result += fraction * (i % radix);
        }
        return result;
    }

    /// <summary>A pixel offset as the clip-space translation <see cref="Apply"/> adds, <c>(2 * px / width,
    /// -2 * py / height)</c>. The y term is negated because clip y points up and pixel y points down. Zero for a zero
    /// offset or an empty target.</summary>
    public static Vector2 ClipOffset(Vector2 jitterPixels, int width, int height)
    {
        if (jitterPixels == Vector2.Zero || width <= 0 || height <= 0) return Vector2.Zero;
        return new Vector2(2f * jitterPixels.X / width, -2f * jitterPixels.Y / height);
    }

    /// <summary>
    /// <paramref name="projection"/> jittered by <paramref name="jitterPixels"/> in a <paramref name="width"/> by
    /// <paramref name="height"/> target: <c>projection * J</c>. Any matrix whose fourth column produces clip w works, so
    /// a view-projection jitters the same way. A zero offset returns the input unchanged, bit for bit, which is what
    /// keeps a frame with temporal rendering off identical to one rendered before jitter existed.
    /// </summary>
    public static Matrix4x4 Apply(in Matrix4x4 projection, Vector2 jitterPixels, int width, int height)
    {
        Vector2 j = ClipOffset(jitterPixels, width, height);
        if (j == Vector2.Zero) return projection;
        Matrix4x4 m = projection;
        // Row-vector convention (clip = row * matrix): column 4 carries clip w, so adding w times the offset to
        // columns 1 and 2 is the product with J written out, without the multiplications by zero.
        m.M11 += m.M14 * j.X; m.M12 += m.M14 * j.Y;
        m.M21 += m.M24 * j.X; m.M22 += m.M24 * j.Y;
        m.M31 += m.M34 * j.X; m.M32 += m.M34 * j.Y;
        m.M41 += m.M44 * j.X; m.M42 += m.M44 * j.Y;
        return m;
    }
}
