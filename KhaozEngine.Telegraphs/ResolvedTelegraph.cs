using KhaozEngine.Primitives;

namespace KhaozEngine.Telegraphs
{
    /// <summary>
    /// The concrete per-frame visual a renderer draws: final fill/outline colors (alphas already multiplied by
    /// opacity + pulse), the swept fill fraction (0..1 of the shape's extent that is filled this frame), an
    /// additive impact-flash term (0..1), and the blend/fill mode carried through from the style. Produced by
    /// <see cref="TelegraphResolve.Resolve"/>; holds no shape geometry (the renderer applies
    /// <see cref="FillFraction"/> to the shape).
    /// </summary>
    public readonly struct ResolvedTelegraph
    {
        public readonly Color FillColor;
        public readonly Color OutlineColor;
        public readonly float FillFraction;
        public readonly float FlashAdd;
        public readonly float EdgeThickness;
        public readonly FillMode FillMode;
        public readonly TelegraphBlend Blend;

        /// <summary>
        /// Feather width as a fraction of the telegraphed shape's extent (0..1). Controls edge softness.
        /// </summary>
        public readonly float FeatherFraction;

        /// <summary>
        /// The fill pattern style (e.g. Solid, RadialNoise). Determines how the fill interior is rendered.
        /// </summary>
        public readonly TelegraphFillPattern Pattern;

        /// <summary>
        /// Pattern animation speed multiplier (cycles per impact window). 0 means no animation.
        /// </summary>
        public readonly float PatternSpeed;

        /// <summary>
        /// Pattern scale / frequency. Controls the size or density of repeating pattern elements.
        /// </summary>
        public readonly float PatternScale;

        /// <summary>
        /// Rim glow intensity (0..1), modulated by edge energy and oscillating with progress. 0 if RimGlow flag is off.
        /// </summary>
        public readonly float RimGlow;

        /// <summary>
        /// Sweep glow intensity (0..1), fading through the impact window. 0 if both SweepGlow and FillSweep flags are off.
        /// </summary>
        public readonly float SweepGlow;

        /// <summary>
        /// Edge sparkle energy (0..1). Multiplied by edge energy. 0 if EdgeSparkle flag is off.
        /// </summary>
        public readonly float Sparkle;

        public ResolvedTelegraph(Color fillColor, Color outlineColor, float fillFraction, float flashAdd,
            float edgeThickness, FillMode fillMode, TelegraphBlend blend)
            : this(fillColor, outlineColor, fillFraction, flashAdd, edgeThickness, fillMode, blend,
                0f, TelegraphFillPattern.Solid, 0f, 0f, 0f, 0f, 0f)
        {
        }

        public ResolvedTelegraph(Color fillColor, Color outlineColor, float fillFraction, float flashAdd,
            float edgeThickness, FillMode fillMode, TelegraphBlend blend,
            float featherFraction, TelegraphFillPattern pattern, float patternSpeed,
            float patternScale, float rimGlow, float sweepGlow, float sparkle)
        {
            FillColor = fillColor;
            OutlineColor = outlineColor;
            FillFraction = fillFraction;
            FlashAdd = flashAdd;
            EdgeThickness = edgeThickness;
            FillMode = fillMode;
            Blend = blend;
            FeatherFraction = featherFraction;
            Pattern = pattern;
            PatternSpeed = patternSpeed;
            PatternScale = patternScale;
            RimGlow = rimGlow;
            SweepGlow = sweepGlow;
            Sparkle = sparkle;
        }
    }
}
