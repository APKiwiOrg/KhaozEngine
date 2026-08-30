using KhaozEngine.Primitives;

namespace KhaozEngine.Telegraphs
{
    /// <summary>
    /// The concrete per-frame visual a renderer draws: final fill/outline colors (alphas already multiplied by
    /// opacity + pulse), the swept fill fraction (0..1 of the shape's extent that is filled this frame), an
    /// additive impact-flash term (0..1), and the blend/fill mode carried through from the style. Also carries
    /// the resolved feather width, fill pattern (with its speed and scale), and the rim/sweep/sparkle edge-energy
    /// terms that drive the modern ground-decal renderer's glow effects. Produced by
    /// <see cref="TelegraphResolve.Resolve"/>; holds no shape geometry (the renderer applies
    /// <see cref="FillFraction"/> to the shape).
    /// <para>
    /// <b>Construct one with an object initializer</b>, naming each member. Every member is
    /// <c>init</c>-settable and every default is the inert one (0, <see cref="TelegraphFillPattern.Solid"/>,
    /// false), so an initializer that sets only what it means is complete. The positional constructors below
    /// are kept for source compatibility and are frozen at their current shapes: a run of ten consecutive
    /// same-typed <c>float</c> parameters in the widest one is not order-checked by the compiler, so any two of
    /// <see cref="RimGlow"/>, <see cref="SweepGlow"/>, <see cref="Sparkle"/>, <see cref="Runner"/> and their
    /// neighbours can be swapped at a call site and still compile, still run, and silently draw the wrong
    /// thing (#126). New state is added as another init member, never as a wider constructor.
    /// </para>
    /// </summary>
    public readonly struct ResolvedTelegraph
    {
        public Color FillColor { get; init; }
        public Color OutlineColor { get; init; }
        public float FillFraction { get; init; }
        public float FlashAdd { get; init; }
        public float EdgeThickness { get; init; }
        public FillMode FillMode { get; init; }
        public TelegraphBlend Blend { get; init; }

        /// <summary>
        /// Feather width as a fraction of the telegraphed shape's extent (0..1). Controls edge softness.
        /// </summary>
        public float FeatherFraction { get; init; }

        /// <summary>
        /// The fill pattern style (e.g. Solid, RadialNoise). Determines how the fill interior is rendered.
        /// </summary>
        public TelegraphFillPattern Pattern { get; init; }

        /// <summary>
        /// Pattern animation speed multiplier (cycles per impact window). 0 means no animation.
        /// </summary>
        public float PatternSpeed { get; init; }

        /// <summary>
        /// Pattern scale / frequency. Controls the size or density of repeating pattern elements.
        /// </summary>
        public float PatternScale { get; init; }

        /// <summary>
        /// Rim glow intensity (0..1), modulated by edge energy and oscillating with progress. 0 if RimGlow flag is off.
        /// </summary>
        public float RimGlow { get; init; }

        /// <summary>
        /// Sweep glow intensity (0..1), fading through the impact window. 0 if both SweepGlow and FillSweep flags are off.
        /// </summary>
        public float SweepGlow { get; init; }

        /// <summary>
        /// Edge sparkle energy (0..1). Multiplied by edge energy. 0 if EdgeSparkle flag is off.
        /// </summary>
        public float Sparkle { get; init; }

        /// <summary>
        /// How much the deep fill interior dims relative to the boundary and sweep front (0 = legacy
        /// uniform fill, 1 = fully hollow). Carried through from the style unchanged.
        /// </summary>
        public float InteriorDim { get; init; }

        /// <summary>
        /// Rotating outline dash-runner intensity. Multiplied by edge energy. 0 if OutlineRunner flag is off.
        /// </summary>
        public float Runner { get; init; }

        /// <summary>
        /// Fraction of the fill alpha painted across the entire shape from progress 0 (0 = legacy, the fill
        /// only shows where the sweep has reached). Carried through from the style clamped to 0..1.
        /// </summary>
        public float BaseFill { get; init; }

        /// <summary>World-unit override for the 3D ground-decal outline / AA edge half-width. 0 keeps
        /// the derived auto-scaling edge. Carried through from the style clamped to non-negative. The
        /// 2D renderer ignores it.</summary>
        public float EdgeWidthWorld { get; init; }

        /// <summary>World-unit override for the 3D ground-decal feather band. 0 keeps the
        /// shape-relative <see cref="FeatherFraction"/> behavior. Carried through from the style
        /// clamped to non-negative. The 2D renderer ignores it.</summary>
        public float FeatherWidthWorld { get; init; }

        /// <summary>Whether the 3D ground decal projects onto its own horizontal plane where there is no
        /// scene geometry, instead of truncating at the geometry's edge. Carried through from the style
        /// unchanged. The 2D renderer ignores it.</summary>
        public bool VoidFallback { get; init; }

        /// <summary>Alpha scale applied only to void-projected pixels of a <see cref="VoidFallback"/> decal.
        /// 0 = no dim. Carried through from the style clamped to 0..1. The 2D renderer ignores it.</summary>
        public float VoidDim { get; init; }

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
            : this(fillColor, outlineColor, fillFraction, flashAdd, edgeThickness, fillMode, blend,
                featherFraction, pattern, patternSpeed, patternScale, rimGlow, sweepGlow, sparkle, 0f, 0f)
        {
        }

        public ResolvedTelegraph(Color fillColor, Color outlineColor, float fillFraction, float flashAdd,
            float edgeThickness, FillMode fillMode, TelegraphBlend blend,
            float featherFraction, TelegraphFillPattern pattern, float patternSpeed,
            float patternScale, float rimGlow, float sweepGlow, float sparkle,
            float interiorDim, float runner)
            : this(fillColor, outlineColor, fillFraction, flashAdd, edgeThickness, fillMode, blend,
                featherFraction, pattern, patternSpeed, patternScale, rimGlow, sweepGlow, sparkle,
                interiorDim, runner, 0f)
        {
        }

        public ResolvedTelegraph(Color fillColor, Color outlineColor, float fillFraction, float flashAdd,
            float edgeThickness, FillMode fillMode, TelegraphBlend blend,
            float featherFraction, TelegraphFillPattern pattern, float patternSpeed,
            float patternScale, float rimGlow, float sweepGlow, float sparkle,
            float interiorDim, float runner, float baseFill)
            : this(fillColor, outlineColor, fillFraction, flashAdd, edgeThickness, fillMode, blend,
                featherFraction, pattern, patternSpeed, patternScale, rimGlow, sweepGlow, sparkle,
                interiorDim, runner, baseFill, 0f, 0f)
        {
        }

        public ResolvedTelegraph(Color fillColor, Color outlineColor, float fillFraction, float flashAdd,
            float edgeThickness, FillMode fillMode, TelegraphBlend blend,
            float featherFraction, TelegraphFillPattern pattern, float patternSpeed,
            float patternScale, float rimGlow, float sweepGlow, float sparkle,
            float interiorDim, float runner, float baseFill, float edgeWidthWorld, float featherWidthWorld)
            : this(fillColor, outlineColor, fillFraction, flashAdd, edgeThickness, fillMode, blend,
                featherFraction, pattern, patternSpeed, patternScale, rimGlow, sweepGlow, sparkle,
                interiorDim, runner, baseFill, edgeWidthWorld, featherWidthWorld, false, 0f)
        {
        }

        public ResolvedTelegraph(Color fillColor, Color outlineColor, float fillFraction, float flashAdd,
            float edgeThickness, FillMode fillMode, TelegraphBlend blend,
            float featherFraction, TelegraphFillPattern pattern, float patternSpeed,
            float patternScale, float rimGlow, float sweepGlow, float sparkle,
            float interiorDim, float runner, float baseFill, float edgeWidthWorld, float featherWidthWorld,
            bool voidFallback, float voidDim)
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
            InteriorDim = interiorDim;
            Runner = runner;
            BaseFill = baseFill;
            EdgeWidthWorld = edgeWidthWorld;
            FeatherWidthWorld = featherWidthWorld;
            VoidFallback = voidFallback;
            VoidDim = voidDim;
        }
    }
}
