using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The material texture mip bias under temporal upscaling (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 5 and
    /// amendment 7). A texture sampled at the internal resolution picks a blurrier mip than the display needs, by
    /// log2(internal / display). The bias undoes that, plus <see cref="TemporalSettings.MipBiasOffset"/>. It reaches
    /// the material programs through the frame block's free <c>Params.z</c> (the bias in mips) and <c>Params.w</c>
    /// (<c>exp2(bias) - 1</c>, the gradient scale of the explicit-gradient ground taps minus one). Both lanes are exact
    /// zeros unless the render is one the temporal resolve accumulates, so the frame block and every tap are unchanged
    /// otherwise.
    /// </summary>
    internal static class TemporalMipBias
    {
        /// <summary>The range <see cref="TemporalSettings.MipBiasOffset"/> is clamped to.</summary>
        internal const float MinOffset = -2f, MaxOffset = 1f;

        /// <summary>The range the bias itself is clamped to. Every preset and ratio falls well inside it, and it
        /// bounds a render cap far below the display.</summary>
        internal const float MinBias = -4f, MaxBias = 2f;

        /// <summary>
        /// The frame block's <c>Params.z</c> and <c>Params.w</c> for one render. <paramref name="resolves"/> is whether
        /// the temporal resolve accumulates this render, and exact zeros come back when it does not.
        /// <paramref name="displayOverInternal"/> is the display over internal scale per axis, unrounded and with the
        /// render cap included, the scale <see cref="TemporalJitter.PhaseCount"/> reads, so the bias is
        /// <c>-log2(displayOverInternal) + offset</c>. A scale that is not a positive finite number gives no bias.
        /// <paramref name="offset"/> is clamped to <see cref="MinOffset"/> to <see cref="MaxOffset"/>, and one that is
        /// not finite counts as 0, so NaN and infinity never reach a shader.
        /// </summary>
        public static Vector2 For(bool resolves, float displayOverInternal, float offset)
        {
            if (!resolves || !float.IsFinite(displayOverInternal) || displayOverInternal <= 0f)
                return Vector2.Zero;
            double o = float.IsFinite(offset) ? Math.Clamp(offset, MinOffset, MaxOffset) : 0.0;
            double bias = Math.Clamp(o - Math.Log2(displayOverInternal), MinBias, MaxBias);
            return new Vector2((float)bias, (float)(Math.Pow(2.0, bias) - 1.0));
        }
    }
}
