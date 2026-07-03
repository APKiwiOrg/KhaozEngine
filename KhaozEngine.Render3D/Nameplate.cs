using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// One horizontal bar on a <see cref="Nameplate"/> (a health/resource meter): a <see cref="Fill"/>-coloured
    /// portion <see cref="Fraction"/> of the way across, over a <see cref="Track"/>-coloured background. Data-only;
    /// <see cref="NameplateRenderer"/> lays it out and draws it.
    /// </summary>
    public readonly struct NameplateBar
    {
        /// <summary>Filled proportion, clamped to 0..1 at draw (see <see cref="ClampedFraction"/>).</summary>
        public readonly float Fraction;
        /// <summary>Colour of the filled portion.</summary>
        public readonly Color Fill;
        /// <summary>Colour of the empty/background portion (the whole bar rect).</summary>
        public readonly Color Track;
        /// <summary>Reserved for a future centred label (e.g. "100/100"). NOT drawn in v1.</summary>
        public readonly string? Overlay;

        public NameplateBar(float fraction, Color fill, Color track, string? overlay = null)
        {
            Fraction = fraction; Fill = fill; Track = track; Overlay = overlay;
        }

        /// <summary><see cref="Fraction"/> clamped to the 0..1 range the renderer draws with.</summary>
        public float ClampedFraction => Math.Clamp(Fraction, 0f, 1f);
    }

    /// <summary>
    /// A world-space nameplate model: a title plus zero or more <see cref="NameplateBar"/>s (e.g. a player name and
    /// a health bar). Data-driven so a game can start with one bar and add more without a rewrite; it supersedes the
    /// text-only <see cref="WorldLabel"/>. Pair it with a <see cref="NameplateStyle"/> and hand both to
    /// <see cref="NameplateRenderer.Draw"/>. An empty title with no bars (<see cref="IsEmpty"/>) is culled.
    /// </summary>
    /// <remarks>
    /// The <c>badges</c>, <c>subtitle</c>, and <c>level</c> mentioned in the design are intentionally NOT fields yet
    /// - they are future additive extensions, added when a game needs them.
    /// </remarks>
    public struct Nameplate
    {
        /// <summary>The name/heading drawn in the top padded row (centred).</summary>
        public string Title;
        /// <summary>Title text colour.</summary>
        public Color TitleColor;
        /// <summary>The bars stacked below the title; may be null or empty for a title-only plate.</summary>
        public IReadOnlyList<NameplateBar> Bars;

        /// <summary>True when there is nothing to draw (blank title AND no bars): the renderer treats this as a cull.</summary>
        public readonly bool IsEmpty => string.IsNullOrEmpty(Title) && (Bars is null || Bars.Count == 0);
    }

    /// <summary>
    /// The look of a <see cref="Nameplate"/> - panel, padding, and bar geometry - separated from its data so one
    /// widget covers the archetypes: the opaque unified plate (<see cref="Default"/>), and a panel-less "classic"
    /// pill (set <see cref="PanelFill"/> alpha to 0 and supply a <see cref="TitleShadow"/> for readability).
    /// </summary>
    public readonly struct NameplateStyle
    {
        /// <summary>Panel background fill. Alpha 0 =&gt; panel-less look (the fill draw is skipped).</summary>
        public Color PanelFill { get; init; }
        /// <summary>Panel border colour (drawn only when <see cref="PanelBorderThickness"/> &gt; 0 and alpha &gt; 0).</summary>
        public Color PanelBorder { get; init; }
        /// <summary>Border stroke width in screen pixels; 0 = no border.</summary>
        public float PanelBorderThickness { get; init; }
        /// <summary>Panel corner radius in screen pixels.</summary>
        public float CornerRadius { get; init; }
        /// <summary>Panel interior horizontal padding.</summary>
        public float PadX { get; init; }
        /// <summary>Panel interior vertical padding.</summary>
        public float PadY { get; init; }
        /// <summary>Height of each bar row in screen pixels.</summary>
        public float BarHeight { get; init; }
        /// <summary>Gap title-&gt;bars and bar-&gt;bar in screen pixels.</summary>
        public float BarSpacing { get; init; }
        /// <summary>Corner radius of each bar rect.</summary>
        public float BarCornerRadius { get; init; }
        /// <summary>The panel is never narrower than this inner width (so short names still get a plate-sized bar).</summary>
        public float MinBarWidth { get; init; }
        /// <summary>Constant on-screen title scale (no distance scaling).</summary>
        public float FontScale { get; init; }
        /// <summary>0 = unbounded; else the panel outer width is capped here and the title is ellipsized to fit.</summary>
        public float MaxWidth { get; init; }
        /// <summary>Optional: draw the title once offset in this colour first (readability for panel-less looks).</summary>
        public Color? TitleShadow { get; init; }
        /// <summary>Pixel offset of the <see cref="TitleShadow"/> pass.</summary>
        public Vector2 TitleShadowOffset { get; init; }

        /// <summary>
        /// The unified-plate preset: an opaque dark rounded panel, a subtle light border, one-bar geometry, no title
        /// shadow. Tweak it with a <c>with</c> expression, e.g. <c>NameplateStyle.Default with { PanelFill =
        /// NameplateStyle.Default.PanelFill.WithAlpha(0f), TitleShadow = Color.Black }</c> for the classic pill.
        /// </summary>
        public static NameplateStyle Default => new NameplateStyle
        {
            PanelFill = new Color(0.08f, 0.09f, 0.11f, 0.92f),
            PanelBorder = new Color(1f, 1f, 1f, 0.16f),
            PanelBorderThickness = 1f,
            CornerRadius = 6f,
            PadX = 8f,
            PadY = 5f,
            BarHeight = 7f,
            BarSpacing = 3f,
            BarCornerRadius = 2f,
            MinBarWidth = 90f,
            FontScale = 1f,
            MaxWidth = 0f,
            TitleShadow = null,
            TitleShadowOffset = new Vector2(1f, 1f),
        };
    }
}
