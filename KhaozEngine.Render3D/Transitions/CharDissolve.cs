using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A world-space transition that dissolves a supplied skinned character out at the origin and back in at the
    /// destination via a noise-thresholded alpha clip with an emissive edge. The world stays visible, so it assumes an
    /// already-streamed destination and never holds. Dissolves out over ~0.25s, swaps (camera warp + reposition) at
    /// full dissolve, then dissolves in over ~0.25s. The character draw reads <see cref="ITransition.Cover"/> as the
    /// dissolve threshold (0 = solid, 1 = gone) plus <see cref="EdgeColor"/> / <see cref="EdgeWidth"/> for the glowing
    /// edge.
    /// </summary>
    public sealed class CharDissolve : Transition
    {
        /// <summary>The emissive colour of the advancing dissolve edge (default a warm white).</summary>
        public Color EdgeColor { get; }

        /// <summary>The width of the glowing dissolve edge as a fraction of the 0..1 threshold range (default 0.08).</summary>
        public float EdgeWidth { get; }

        /// <summary>Creates a character dissolve. Never holds (assumes a streamed destination). Defaults: dissolve out
        /// ~0.25s, dissolve in ~0.25s, warm-white edge.</summary>
        public CharDissolve(float dissolveOutSeconds = 0.25f, float dissolveInSeconds = 0.25f,
            Color? edgeColor = null, float edgeWidth = 0.08f)
            : base(coverSeconds: dissolveOutSeconds, holdTimeoutSeconds: 0f, revealSeconds: dissolveInSeconds)
        {
            EdgeColor = edgeColor ?? new Color(1f, 0.85f, 0.6f, 1f);
            EdgeWidth = edgeWidth;
        }
    }
}
