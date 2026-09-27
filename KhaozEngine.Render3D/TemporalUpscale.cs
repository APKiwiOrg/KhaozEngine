namespace KhaozEngine.Render3D
{
    /// <summary>
    /// How far below the display the temporal upscaler renders, per axis, while <see cref="AntiAliasing.Temporal"/> is
    /// active. Read through <see cref="TemporalSettings.Upscale"/>. <see cref="TemporalSettings.UpscaleRatio"/> overrides
    /// it with any ratio from 0.33 to 1. A change resets the temporal history for one frame while the preset applies.
    /// The size is then clamped to <see cref="PixelPostProcessSettings.MaxRenderWidth"/> x
    /// <see cref="PixelPostProcessSettings.MaxRenderHeight"/>, so under the default 3840x2160 cap even Native renders
    /// below a display taller than 2160.
    /// </summary>
    public enum TemporalUpscale
    {
        /// <summary>Render at the display size, up to the render cap. Plain temporal anti-aliasing.</summary>
        Native,
        /// <summary>Render at 1 / 1.5 per axis, 44 percent of the display's pixels.</summary>
        Quality,
        /// <summary>Render at 1 / 1.7 per axis, 35 percent of the display's pixels.</summary>
        Balanced,
        /// <summary>Render at 1 / 2 per axis, 25 percent of the display's pixels.</summary>
        Performance,
        /// <summary>Render at 1 / 3 per axis, 11 percent of the display's pixels. On a 3456x2234 display that is fewer
        /// pixels than a fixed 1600x900 target.</summary>
        UltraPerformance,
    }
}
