using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Telegraphs
{
    /// <summary>
    /// Pure mapping from a 0..1 telegraph progress + a <see cref="TelegraphStyle"/> to a
    /// <see cref="ResolvedTelegraph"/>. No state, no allocation, no randomness - same inputs give the same output,
    /// so feeding it from a deterministic sim never perturbs the sim. The renderers apply the result; the shape
    /// geometry is theirs.
    /// </summary>
    public static class TelegraphResolve
    {
        public static ResolvedTelegraph Resolve(float progress, in TelegraphStyle style)
        {
            float p = MathUtil.Clamp01(progress);

            // Fill sweep: dangerous area grows from a small seed to full as impact nears (ease-out so it lingers
            // near full). Off => always full.
            float fillFraction = style.Animation.HasFlag(TelegraphAnim.FillSweep)
                ? MathUtil.Lerp(0.04f, 1f, Easing.EaseOut(p))
                : 1f;

            // Color ramp: lerp the fill RGB from base toward the danger color over progress. Off => base.
            Color fillRgb = style.Animation.HasFlag(TelegraphAnim.ColorRamp)
                ? Color.Lerp(style.FillColor, style.DangerColor, p)
                : style.FillColor;

            // Impact flash: 0 until late, then a sharp rise to ~1 at p=1 (quartic shoulder). Off => 0.
            float flash = style.Animation.HasFlag(TelegraphAnim.ImpactFlash)
                ? FlashCurve(p)
                : 0f;

            // Outline pulse: a 0.5..1 multiplier oscillating a few times across the window. Off => 1.
            float pulse = style.Animation.HasFlag(TelegraphAnim.OutlinePulse)
                ? 0.75f + 0.25f * MathF.Sin(p * MathF.Tau * 3f)
                : 1f;

            float op = MathUtil.Clamp01(style.Opacity);
            Color fill = fillRgb.WithAlpha(MathUtil.Clamp01(fillRgb.A * op));
            Color outline = style.OutlineColor.WithAlpha(MathUtil.Clamp01(style.OutlineColor.A * op * pulse));

            float energy = style.EdgeEnergy > 0f ? style.EdgeEnergy : 1f;

            float rimGlow = 0f;
            if ((style.Animation & TelegraphAnim.RimGlow) != 0)
                rimGlow = energy * (0.65f + 0.35f * MathF.Sin(p * MathF.Tau * 2.5f));

            float sweepGlow = 0f;
            if ((style.Animation & TelegraphAnim.SweepGlow) != 0
                && (style.Animation & TelegraphAnim.FillSweep) != 0)
            {
                // Ramp the glow in over the first ~fifth of the cast: with an eased FillSweep the swept region is
                // still tiny then, and a full-strength glow band wider than the region reads as a bright ball at
                // the shape center rather than a leading edge.
                float rampIn = MathUtil.Clamp01((p - 0.08f) / 0.14f);
                sweepGlow = energy * MathF.Sin(p * MathF.PI) * rampIn;
            }

            float sparkle = (style.Animation & TelegraphAnim.EdgeSparkle) != 0 ? energy : 0f;

            float runner = (style.Animation & TelegraphAnim.OutlineRunner) != 0 ? energy : 0f;

            // Honor FillMode at the resolve level so BOTH renderers agree (the 2D renderer picks primitives by
            // FillMode, the 3D decal path draws whatever alphas it is handed and historically ignored the mode).
            // Fill: no outline band and no outline-band effects (rim, runner). Outline: no fill (which also
            // silences the fill-driven pattern, base fill, and sweep glow in the shader).
            if (style.FillMode == FillMode.Fill)
            {
                outline = outline.WithAlpha(0f);
                rimGlow = 0f;
                runner = 0f;
            }
            else if (style.FillMode == FillMode.Outline)
            {
                fill = fill.WithAlpha(0f);
            }

            // Named members rather than 21 positional arguments: ten of them are consecutive floats the compiler
            // cannot order-check, so a transposed pair would compile and silently draw the wrong glow (#126).
            return new ResolvedTelegraph
            {
                FillColor = fill,
                OutlineColor = outline,
                FillFraction = fillFraction,
                FlashAdd = flash,
                EdgeThickness = style.EdgeThickness,
                FillMode = style.FillMode,
                Blend = style.Blend,
                FeatherFraction = style.FeatherWidth,
                Pattern = style.Pattern,
                PatternSpeed = style.PatternSpeed,
                PatternScale = style.PatternScale,
                RimGlow = rimGlow,
                SweepGlow = sweepGlow,
                Sparkle = sparkle,
                InteriorDim = MathUtil.Clamp01(style.InteriorDim),
                Runner = runner,
                BaseFill = MathUtil.Clamp01(style.BaseFill),
                EdgeWidthWorld = MathF.Max(style.EdgeWidthWorld, 0f),
                FeatherWidthWorld = MathF.Max(style.FeatherWidthWorld, 0f),
                // 3D-only passthrough, like EdgeWidthWorld / FeatherWidthWorld: the resolver does not interpret
                // these, it only carries them to the ground-decal path. TelegraphRenderer2D ignores them.
                VoidFallback = style.VoidFallback,
                VoidDim = MathUtil.Clamp01(style.VoidDim),
                // The accent is a fill-field colour, so its alpha rides the style opacity exactly as the fill's
                // does. A fully zero AccentColor (every style that predates it) stays fully zero at any opacity.
                AccentColor = style.AccentColor.WithAlpha(MathUtil.Clamp01(style.AccentColor.A * op)),
                // Both dimensionless, so neither needs the world-unit derivation FeatherFraction gets on the 3D
                // path. Carried, not interpreted, like the two World overrides above.
                PatternParam = MathF.Max(style.PatternParam, 0f),
                EdgeErosion = MathUtil.Clamp01(style.EdgeErosion),
            };
        }

        // 0 below ~0.6, rising steeply to 1 at p=1. Quartic for a snappy late spike.
        static float FlashCurve(float p)
        {
            if (p <= 0.6f) return 0f;
            float t = (p - 0.6f) / 0.4f; // 0..1 over the last 40%
            return t * t * t * t;
        }
    }
}
