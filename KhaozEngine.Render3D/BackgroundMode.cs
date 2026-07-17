namespace KhaozEngine.Render3D
{
    /// <summary>
    /// What fills the background (the pixels where no geometry was drawn). Mutually exclusive by construction, and
    /// the single ergonomic knob over the underlying <see cref="PixelPostProcessSettings.Starfield"/> /
    /// <see cref="SkySettings.Enabled"/> booleans, which remain the storage.
    /// </summary>
    /// <remarks>
    /// Reached via <see cref="PixelPostProcessSettings.Background"/>, which DERIVES this from those booleans rather
    /// than duplicating them, so there is no second source of truth to drift. Orthogonal to
    /// <see cref="PixelPostProcessSettings.TransparentBackground"/>, which controls the final image's output alpha
    /// for offscreen compositing rather than what gets painted. A non-<see cref="Solid"/> background paints opaque
    /// pixels, so a transparent composite needs <see cref="Solid"/>.
    /// </remarks>
    public enum BackgroundMode
    {
        /// <summary>Just the clear colour (<see cref="PixelPostProcessSettings.BackgroundColor"/>).</summary>
        Solid,
        /// <summary>Procedural starfield over the clear colour (assumes a dark space background).</summary>
        Starfield,
        /// <summary>Procedural sky gradient with an optional sun disc, see <see cref="SkySettings"/>.</summary>
        Sky,
    }
}
