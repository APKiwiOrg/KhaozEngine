using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The cheapest built-in transition: a screen-space fade to a solid <see cref="Color"/> (black by default) and
    /// back, like a fast eye-blink. Covers in ~1-2 frames, swaps under the cover, optionally holds for streaming, then
    /// reveals in ~0.08s. Hides the teleport jump and destination pop-in. The scene draws a fullscreen quad of
    /// <see cref="Color"/> at opacity <see cref="ITransition.Cover"/>.
    /// </summary>
    public sealed class HardBlink : Transition
    {
        /// <summary>The colour the screen fades to at full cover (default opaque <see cref="Primitives.Color.Black"/>).
        /// The on-screen opacity each frame is <see cref="ITransition.Cover"/>.</summary>
        public Color Color { get; }

        /// <summary>Creates a hard blink. Defaults: fade out ~0.06s, hold up to 1.5s for streaming, fade in ~0.08s, to
        /// black.</summary>
        public HardBlink(float coverSeconds = 0.06f, float holdTimeoutSeconds = 1.5f, float revealSeconds = 0.08f,
            Color? color = null)
            : base(coverSeconds, holdTimeoutSeconds, revealSeconds)
        {
            Color = color ?? Color.Black;
        }
    }
}
