using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Telegraphs
{
    /// <summary>How a telegraph shape is filled.</summary>
    public enum FillMode { Outline, Fill, OutlineAndFill }

    /// <summary>Compositing for a telegraph (matches the renderer blend states).</summary>
    public enum TelegraphBlend { Alpha, Additive }

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

        /// <summary>Neutral red-orange danger zone: alpha-blended outline + fill, all animations on.</summary>
        public static TelegraphStyle Generic => new()
        {
            FillColor = new Color(0.95f, 0.30f, 0.15f, 0.35f),
            OutlineColor = new Color(1f, 0.55f, 0.25f, 0.9f),
            DangerColor = new Color(1f, 0.10f, 0.05f, 0.55f),
            EdgeThickness = 2f,
            Opacity = 1f,
            FillMode = FillMode.OutlineAndFill,
            Animation = TelegraphAnim.FillSweep | TelegraphAnim.ColorRamp | TelegraphAnim.ImpactFlash,
            Blend = TelegraphBlend.Alpha,
            ZoneSense = ZoneSense.Danger,
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
            Animation = TelegraphAnim.FillSweep | TelegraphAnim.ColorRamp | TelegraphAnim.ImpactFlash | TelegraphAnim.OutlinePulse,
            Blend = TelegraphBlend.Additive,
            ZoneSense = ZoneSense.Danger,
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
            Animation = TelegraphAnim.FillSweep | TelegraphAnim.ColorRamp | TelegraphAnim.OutlinePulse,
            Blend = TelegraphBlend.Alpha,
            ZoneSense = ZoneSense.Danger,
        };
    }
}
