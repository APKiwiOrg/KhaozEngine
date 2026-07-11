using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A world-space transition that MATERIALIZES a supplied skinned character IN at the destination via a
    /// noise-thresholded alpha clip with an emissive edge. A teleport is a server-authoritative hard cut: the
    /// authoritative avatar has already cut to the destination before this effect starts, so there is no origin frame
    /// left to dissolve OUT of (the old two-phase "out at origin then in at destination" spec was unachievable). It
    /// therefore covers instantly (fully dissolved = invisible on the cut frame), swaps (camera warp) under cover, and
    /// dissolves the avatar IN at the destination over its duration. The world stays visible, so it assumes an
    /// already-streamed destination and never holds. The character draw reads <see cref="ITransition.Cover"/> as the
    /// dissolve threshold (0 = solid, 1 = gone) plus <see cref="EdgeColor"/> / <see cref="EdgeWidth"/> for the glowing
    /// edge, so Cover 1 -> 0 across the reveal materializes it in.
    /// <para>An origin-side dissolve-out (holding the avatar rendered at the origin for a render-only beat before the
    /// cut) is a possible future addition via a render-only position hold; it is intentionally NOT built here.</para>
    /// </summary>
    public sealed class CharDissolve : Transition
    {
        /// <summary>The emissive colour of the advancing dissolve edge (default a warm white).</summary>
        public Color EdgeColor { get; }

        /// <summary>The width of the glowing dissolve edge as a fraction of the 0..1 threshold range (default 0.08).</summary>
        public float EdgeWidth { get; }

        /// <summary>Creates a character materialize-in. Covers instantly (no origin dissolve-out) and never holds
        /// (assumes a streamed destination). Defaults: materialize in over ~0.35s, warm-white edge.</summary>
        public CharDissolve(float materializeSeconds = 0.35f, Color? edgeColor = null, float edgeWidth = 0.08f)
            : base(coverSeconds: 0f, holdTimeoutSeconds: 0f, revealSeconds: materializeSeconds)
        {
            EdgeColor = edgeColor ?? new Color(1f, 0.85f, 0.6f, 1f);
            EdgeWidth = edgeWidth;
        }
    }
}
