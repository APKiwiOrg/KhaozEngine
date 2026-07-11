using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The cheapest built-in transition: a screen-space solid <see cref="Color"/> mask (black by default) over the
    /// teleport, like a fast eye-blink. A teleport is a server-authoritative hard cut, so by default this covers
    /// INSTANTLY (<c>coverSeconds = 0</c>: the mask is fully opaque on the very cut frame, no fade-out ramp), swaps
    /// under the cover, optionally holds for streaming, then reveals in ~0.08s. Pass a non-zero <c>coverSeconds</c>
    /// for a soft fade-OUT into the cut instead (a cosmetic pre-roll, not part of the cut). Hides the teleport jump
    /// and destination pop-in. The scene draws a fullscreen quad of <see cref="Color"/> at opacity
    /// <see cref="ITransition.Cover"/>.
    /// </summary>
    public sealed class HardBlink : Transition, IScreenTransition
    {
        /// <inheritdoc/>
        public ScreenTransitionStyle Style => ScreenTransitionStyle.Solid;

        /// <summary>The colour the screen fades to at full cover (default opaque <see cref="Primitives.Color.Black"/>).
        /// The on-screen opacity each frame is <see cref="ITransition.Cover"/>.</summary>
        public Color Color { get; }

        /// <summary>Creates a hard blink. Defaults: cover INSTANTLY (<paramref name="coverSeconds"/> 0, so the mask is
        /// opaque on the cut frame - a hard cut has no fade-out), hold up to 1.5s for streaming, reveal ~0.08s, to
        /// black. Raise <paramref name="coverSeconds"/> only for a cosmetic fade-out into the cut.</summary>
        public HardBlink(float coverSeconds = 0f, float holdTimeoutSeconds = 1.5f, float revealSeconds = 0.08f,
            Color? color = null)
            : base(coverSeconds, holdTimeoutSeconds, revealSeconds)
        {
            Color = color ?? Color.Black;
        }
    }
}
