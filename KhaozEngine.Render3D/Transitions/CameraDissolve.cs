namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A screen-space transition that freezes the last rendered frame and crossfades from that frozen frame to the
    /// live new view. Softer than <see cref="HardBlink"/>: it covers instantly (the captured frame masks the swap),
    /// optionally holds for streaming, then crossfades to the live view over ~0.35s. Good for a far, likely-unstreamed
    /// destination (login/reconnect). The scene captures the frame when this becomes active and blends frozen->live by
    /// <see cref="ITransition.Cover"/> (1 = fully frozen, 0 = fully live).
    /// </summary>
    public sealed class CameraDissolve : Transition, IScreenTransition
    {
        /// <inheritdoc/>
        public ScreenTransitionStyle Style => ScreenTransitionStyle.FrozenCrossfade;

        /// <summary>Unused by the crossfade (it samples the frozen frame); present to satisfy
        /// <see cref="IScreenTransition"/>.</summary>
        public Primitives.Color Color => Primitives.Color.White;

        /// <summary>Creates a camera dissolve. Covers instantly (the frozen frame); defaults: hold up to 1.5s for
        /// streaming, crossfade to the live view over ~0.35s.</summary>
        public CameraDissolve(float holdTimeoutSeconds = 1.5f, float revealSeconds = 0.35f)
            : base(coverSeconds: 0f, holdTimeoutSeconds, revealSeconds) { }
    }
}
