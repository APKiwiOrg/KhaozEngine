using System;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Settings for temporal rendering, reachable as <see cref="PixelPostProcessSettings.Temporal"/> beside
    /// <see cref="PixelPostProcessSettings.Bloom"/> and <see cref="PixelPostProcessSettings.Water"/>. Nothing here costs
    /// anything until something asks for temporal rendering. The thresholds decide when a camera move is a cut: a frame
    /// whose camera moved further than <see cref="CutDistanceMetres"/> or turned more than <see cref="CutAngleDegrees"/>
    /// since the last frame drops its temporal history, exactly as an explicit <see cref="Scene3D.CameraCut"/> does. A
    /// render origin jump the previous frame cannot be rebased across triggers the same automatic cut, whatever the
    /// thresholds, as <see cref="TemporalResetReason.CameraCutDetected"/> describes. The two cuts differ only in the
    /// reason they report: <see cref="TemporalResetReason.CameraCutDetected"/> for the automatic one and
    /// <see cref="TemporalResetReason.CameraCutRequested"/> for the explicit call.
    /// </summary>
    public sealed class TemporalSettings
    {
        /// <summary>The largest distance, in metres, the camera eye may move between two frames and still continue the
        /// previous one. Default 16. <see cref="float.PositiveInfinity"/> turns the distance check off. A render origin
        /// jump the previous frame cannot be rebased across is still a cut, see
        /// <see cref="TemporalResetReason.CameraCutDetected"/>. NaN and negative values are caller errors. They are not
        /// validated and their behaviour is unsupported.</summary>
        public float CutDistanceMetres = 16f;

        /// <summary>The largest angle, in degrees, the camera's forward direction may turn between two frames and still
        /// continue the previous one. Default 60. A value of 180 or more turns the angle check off. NaN and negative
        /// values are caller errors. They are not validated and their behaviour is unsupported.</summary>
        public float CutAngleDegrees = 60f;

        /// <summary>The smallest internal-over-display ratio <see cref="UpscaleRatio"/> accepts, a third per axis.</summary>
        internal const float MinUpscaleRatio = 0.33f;

        /// <summary>The internal resolution preset while <see cref="AntiAliasing.Temporal"/> is active. Default
        /// <see cref="TemporalUpscale.Native"/>. A change resets the temporal history for one frame.</summary>
        public TemporalUpscale Upscale = TemporalUpscale.Native;

        /// <summary>An explicit internal-over-display ratio per axis that overrides <see cref="Upscale"/> when set, clamped
        /// to 0.33 to 1. A value that is not finite is ignored and the preset applies. A change resets the temporal
        /// history for one frame.</summary>
        public float? UpscaleRatio;

        /// <summary>The display-over-internal factor per axis a preset stands for: 1, 1.5, 1.7, 2 or 3.</summary>
        internal static float DisplayOverInternal(TemporalUpscale preset) => preset switch
        {
            TemporalUpscale.Quality => 1.5f,
            TemporalUpscale.Balanced => 1.7f,
            TemporalUpscale.Performance => 2f,
            TemporalUpscale.UltraPerformance => 3f,
            _ => 1f,
        };

        /// <summary>The internal-over-display ratio per axis in effect: <see cref="UpscaleRatio"/> clamped when it is set
        /// and finite, else the reciprocal of the preset's factor.</summary>
        internal float ResolvedUpscaleRatio => UpscaleRatio is float ratio && float.IsFinite(ratio)
            ? Math.Clamp(ratio, MinUpscaleRatio, 1f)
            : 1f / DisplayOverInternal(Upscale);
    }
}
