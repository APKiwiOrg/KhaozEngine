using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Telegraphs
{
    /// <summary>How a telegraph shape is filled.</summary>
    public enum FillMode { Outline, Fill, OutlineAndFill }

    /// <summary>Compositing for a telegraph (matches the renderer blend states).</summary>
    public enum TelegraphBlend { Alpha, Additive }

    /// <summary>Fill pattern for the telegraph interior. Solid is the legacy flat tint.</summary>
    public enum TelegraphFillPattern
    {
        Solid = 0,
        /// <summary>Domain-warped value noise drifting across the shape into wispy filaments, tinted by
        /// the fill color.</summary>
        ScrollingNoise = 1,
        /// <summary>Cartesian vortex swirl, spiral arms orbiting the shape center over time.</summary>
        RadialNoise = 2,
    }

    /// <summary>
    /// Whether the shape marks the DANGER area (default) or, RESERVED for a future version, the SAFE area
    /// (everything-dangerous-except-here). v1 renders <see cref="Safe"/> exactly like <see cref="Danger"/>.
    /// </summary>
    public enum ZoneSense { Danger, Safe }

    /// <summary>
    /// Progress-driven animation behaviours, composable. <see cref="OutlinePulse"/> oscillates the outline alpha;
    /// <see cref="FillSweep"/> grows the filled area as impact nears; <see cref="ColorRamp"/> lerps the fill toward
    /// a danger color; <see cref="ImpactFlash"/> spikes brightness near progress 1.
    /// </summary>
    [Flags]
    public enum TelegraphAnim
    {
        None = 0,
        OutlinePulse = 1 << 0,
        FillSweep = 1 << 1,
        ColorRamp = 1 << 2,
        ImpactFlash = 1 << 3,
        /// <summary>Soft glow hugging the shape boundary, pulsing with cast progress.</summary>
        RimGlow = 1 << 4,
        /// <summary>Bright soft leading edge on the FillSweep front. Requires FillSweep.</summary>
        SweepGlow = 1 << 5,
        /// <summary>Sparse animated sparkle cells along the shape boundary.</summary>
        EdgeSparkle = 1 << 6,
        /// <summary>Rotating dash segments orbiting the outline band (a rune-ring feel).</summary>
        OutlineRunner = 1 << 7,
    }

    /// <summary>
    /// Styling for a telegraph shape: colors, edge thickness, opacity, fill mode, animation flags, blend, and the
    /// reserved zone sense. A plain value type; use the presets (<see cref="Generic"/>, <see cref="Fire"/>,
    /// <see cref="Poison"/>) and `with`-style copies to tweak. Consumed (with a 0..1 progress) by
    /// <see cref="TelegraphResolve"/>.
    /// </summary>
    public struct TelegraphStyle
    {
        /// <summary>Base fill color (RGB). The "safe" end of the color ramp; alpha is the fill's base opacity.</summary>
        public Color FillColor;
        /// <summary>Outline color (RGBA). Alpha is the outline's base opacity.</summary>
        public Color OutlineColor;
        /// <summary>The "danger" end of the color ramp the fill lerps toward as progress -> 1.</summary>
        public Color DangerColor;
        /// <summary>Outline / ring-band / feathered-edge width, in the renderer's units (pixels for 2D, world for 3D).</summary>
        public float EdgeThickness;
        /// <summary>Master opacity multiplier applied on top of the per-color alphas (0..1).</summary>
        public float Opacity;
        public FillMode FillMode;
        public TelegraphAnim Animation;
        public TelegraphBlend Blend;
        /// <summary>Whether the shape marks the danger area (default) or the safe area. RESERVED: the v1 resolver
        /// and renderers ignore this, so <see cref="ZoneSense.Safe"/> currently renders identically to
        /// <see cref="ZoneSense.Danger"/>. Kept so styles / presets can declare intent ahead of the feature.</summary>
        public ZoneSense ZoneSense;

        /// <summary>Soft feather band on the shape boundary, as a fraction of the shape's
        /// characteristic size. 0 keeps the legacy hard anti-aliased edge. Modern presets use
        /// roughly 0.06 (crisp) to 0.18 (soft).</summary>
        public float FeatherWidth;

        /// <summary>Interior fill pattern. Solid keeps the legacy flat tint.</summary>
        public TelegraphFillPattern Pattern;

        /// <summary>Pattern animation speed in cycles per second of the scene effect clock.</summary>
        public float PatternSpeed;

        /// <summary>Noise cells across the shape's characteristic size. 0 falls back to 6.</summary>
        public float PatternScale;

        /// <summary>Master strength multiplier for RimGlow, SweepGlow and EdgeSparkle.
        /// 0 means the default full strength of 1. Explicit values scale the effects.</summary>
        public float EdgeEnergy;

        /// <summary>How much the deep interior of the fill dims relative to the boundary and sweep front
        /// (0 = legacy uniform fill, 1 = fully hollow). Concentrates the energy at the rim and the moving
        /// sweep edge, the modern look. Modern presets use roughly 0.35 (dense) to 0.6 (hollow).</summary>
        public float InteriorDim;

        /// <summary>Fraction of the fill alpha painted across the ENTIRE shape from progress 0, before and
        /// under the sweep (0 = legacy, nothing shows until the sweep reaches it). Lets the full danger extent
        /// read immediately without any outline, the borderless look. Modern presets use 0.3.</summary>
        public float BaseFill;

        /// <summary>Opt-in world-unit override for the 3D ground-decal outline / AA edge half-width.
        /// 0 (default) keeps the derived auto-scaling edge (a small fraction of the shape's
        /// characteristic size, clamped). Set a positive value to pin the stroke in world units at any
        /// shape size, e.g. a thin crisp range ring at a large radius. The outline band's solid core
        /// renders about twice this value across the boundary. The 2D <c>TelegraphRenderer2D</c>
        /// ignores this field (it strokes in pixels via <see cref="EdgeThickness"/>).</summary>
        public float EdgeWidthWorld;

        /// <summary>Opt-in world-unit override for the 3D ground-decal feather band. 0 (default) keeps
        /// the shape-relative <see cref="FeatherWidth"/> fraction behavior. Set a positive value to pin
        /// the feather in world units regardless of shape size. The 2D <c>TelegraphRenderer2D</c>
        /// ignores this field.</summary>
        public float FeatherWidthWorld;

        /// <summary>Neutral red-orange danger zone: alpha-blended outline + fill, all animations on.</summary>
        public static TelegraphStyle Generic => new()
        {
            FillColor = new Color(0.95f, 0.30f, 0.15f, 0.35f),
            OutlineColor = new Color(1f, 0.55f, 0.25f, 0.9f),
            DangerColor = new Color(1f, 0.10f, 0.05f, 0.55f),
            EdgeThickness = 2f,
            Opacity = 1f,
            FillMode = FillMode.OutlineAndFill,
            Animation = TelegraphAnim.FillSweep | TelegraphAnim.ColorRamp | TelegraphAnim.ImpactFlash
                      | TelegraphAnim.RimGlow | TelegraphAnim.SweepGlow,
            Blend = TelegraphBlend.Alpha,
            ZoneSense = ZoneSense.Danger,
            FeatherWidth = 0.10f,
            Pattern = TelegraphFillPattern.ScrollingNoise,
            PatternSpeed = 0.35f,
            PatternScale = 6f,
            InteriorDim = 0.45f,
            BaseFill = 0.3f,
        };

        /// <summary>Fiery additive variant (warm ramp, glows over the scene).</summary>
        public static TelegraphStyle Fire => new()
        {
            FillColor = new Color(1f, 0.55f, 0.10f, 0.30f),
            OutlineColor = new Color(1f, 0.80f, 0.30f, 0.9f),
            DangerColor = new Color(1f, 0.20f, 0.02f, 0.6f),
            EdgeThickness = 2f,
            Opacity = 1f,
            FillMode = FillMode.OutlineAndFill,
            Animation = TelegraphAnim.FillSweep | TelegraphAnim.ColorRamp | TelegraphAnim.ImpactFlash | TelegraphAnim.OutlinePulse
                      | TelegraphAnim.RimGlow | TelegraphAnim.SweepGlow | TelegraphAnim.EdgeSparkle,
            Blend = TelegraphBlend.Additive,
            ZoneSense = ZoneSense.Danger,
            FeatherWidth = 0.12f,
            Pattern = TelegraphFillPattern.ScrollingNoise,
            PatternSpeed = 0.9f,
            PatternScale = 7f,
            InteriorDim = 0.5f,
            BaseFill = 0.3f,
        };

        /// <summary>Toxic green variant (alpha-blended, pulsing outline).</summary>
        public static TelegraphStyle Poison => new()
        {
            FillColor = new Color(0.35f, 0.85f, 0.20f, 0.32f),
            OutlineColor = new Color(0.6f, 1f, 0.35f, 0.9f),
            DangerColor = new Color(0.30f, 1f, 0.10f, 0.55f),
            EdgeThickness = 2f,
            Opacity = 1f,
            FillMode = FillMode.OutlineAndFill,
            Animation = TelegraphAnim.FillSweep | TelegraphAnim.ColorRamp | TelegraphAnim.OutlinePulse
                      | TelegraphAnim.RimGlow | TelegraphAnim.SweepGlow,
            Blend = TelegraphBlend.Alpha,
            ZoneSense = ZoneSense.Danger,
            FeatherWidth = 0.14f,
            Pattern = TelegraphFillPattern.ScrollingNoise,
            PatternSpeed = 0.45f,
            PatternScale = 5f,
            InteriorDim = 0.5f,
            BaseFill = 0.3f,
        };

        /// <summary>Physical/steel telegraph: cool grey, crisp edge, fine brushed grain.</summary>
        public static TelegraphStyle Steel => new()
        {
            FillColor = new Color(0.62f, 0.68f, 0.75f, 0.30f),
            OutlineColor = new Color(0.85f, 0.92f, 1f, 0.95f),
            DangerColor = new Color(0.95f, 0.35f, 0.25f, 0.55f),
            EdgeThickness = 2f,
            Opacity = 1f,
            FillMode = FillMode.OutlineAndFill,
            Animation = TelegraphAnim.FillSweep | TelegraphAnim.ColorRamp | TelegraphAnim.ImpactFlash
                      | TelegraphAnim.SweepGlow | TelegraphAnim.OutlineRunner,
            Blend = TelegraphBlend.Alpha,
            ZoneSense = ZoneSense.Danger,
            FeatherWidth = 0.06f,
            Pattern = TelegraphFillPattern.ScrollingNoise,
            PatternSpeed = 0.25f,
            PatternScale = 9f,
            InteriorDim = 0.35f,
            BaseFill = 0.3f,
        };

        /// <summary>Frost telegraph: pale ice blue, wide soft feather, slow crystalline flow.</summary>
        public static TelegraphStyle Frost => new()
        {
            FillColor = new Color(0.55f, 0.80f, 1f, 0.30f),
            OutlineColor = new Color(0.80f, 0.95f, 1f, 0.95f),
            DangerColor = new Color(0.35f, 0.60f, 1f, 0.60f),
            EdgeThickness = 2f,
            Opacity = 1f,
            FillMode = FillMode.OutlineAndFill,
            Animation = TelegraphAnim.FillSweep | TelegraphAnim.ColorRamp | TelegraphAnim.OutlinePulse
                      | TelegraphAnim.RimGlow | TelegraphAnim.EdgeSparkle,
            Blend = TelegraphBlend.Alpha,
            ZoneSense = ZoneSense.Danger,
            FeatherWidth = 0.18f,
            Pattern = TelegraphFillPattern.RadialNoise,
            PatternSpeed = 0.2f,
            PatternScale = 5f,
            InteriorDim = 0.6f,
            BaseFill = 0.3f,
        };

        /// <summary>Nature telegraph: verdant green, soft organic drift.</summary>
        public static TelegraphStyle Nature => new()
        {
            FillColor = new Color(0.30f, 0.75f, 0.30f, 0.32f),
            OutlineColor = new Color(0.55f, 1f, 0.45f, 0.90f),
            DangerColor = new Color(0.85f, 0.95f, 0.20f, 0.55f),
            EdgeThickness = 2f,
            Opacity = 1f,
            FillMode = FillMode.OutlineAndFill,
            Animation = TelegraphAnim.FillSweep | TelegraphAnim.ColorRamp | TelegraphAnim.RimGlow
                      | TelegraphAnim.SweepGlow,
            Blend = TelegraphBlend.Alpha,
            ZoneSense = ZoneSense.Danger,
            FeatherWidth = 0.16f,
            Pattern = TelegraphFillPattern.ScrollingNoise,
            PatternSpeed = 0.3f,
            PatternScale = 4f,
            InteriorDim = 0.5f,
            BaseFill = 0.3f,
        };

        /// <summary>Arcane telegraph: violet additive energy, radial pulse, full edge energy.</summary>
        public static TelegraphStyle Arcane => new()
        {
            FillColor = new Color(0.60f, 0.30f, 1f, 0.30f),
            OutlineColor = new Color(0.85f, 0.55f, 1f, 0.95f),
            DangerColor = new Color(1f, 0.30f, 0.90f, 0.60f),
            EdgeThickness = 2f,
            Opacity = 1f,
            FillMode = FillMode.OutlineAndFill,
            Animation = TelegraphAnim.FillSweep | TelegraphAnim.ColorRamp | TelegraphAnim.ImpactFlash
                      | TelegraphAnim.OutlinePulse | TelegraphAnim.RimGlow | TelegraphAnim.SweepGlow
                      | TelegraphAnim.EdgeSparkle | TelegraphAnim.OutlineRunner,
            Blend = TelegraphBlend.Additive,
            ZoneSense = ZoneSense.Danger,
            FeatherWidth = 0.12f,
            Pattern = TelegraphFillPattern.RadialNoise,
            PatternSpeed = 0.6f,
            PatternScale = 6f,
            InteriorDim = 0.6f,
            BaseFill = 0.3f,
        };
    }
}
