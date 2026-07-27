namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A named sky + lighting bundle applied in one call via <see cref="EnvironmentPresets.Apply"/>. Each value sets
    /// the same coherent group of fields on <see cref="PixelPostProcessSettings"/> (the sky palette, the background
    /// mode, and the five lighting fields), so a map editor or sample can offer a simple dropdown instead of
    /// exposing every knob individually.
    /// </summary>
    public enum EnvironmentPresetKind
    {
        /// <summary>Bright midday sky, warm key light. Close to <see cref="SkySettings"/>'s own defaults, and the
        /// recommended map-editor default.</summary>
        Day,

        /// <summary>Warm orange/pink horizon fading to a deep blue/purple zenith, a low warm sun.</summary>
        Sunset,

        /// <summary>Very dark blue sky gradient, faint cool moonlight key, dark ambient.</summary>
        Night,

        /// <summary>Procedural starfield background instead of a sky pass. The sky palette is pulled down to match
        /// the near-black background so reflective water has no bright horizon to seam against.</summary>
        Starfield,
    }
}
