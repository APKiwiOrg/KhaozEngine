namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Settings for temporal rendering, reachable as <see cref="PixelPostProcessSettings.Temporal"/> beside
    /// <see cref="PixelPostProcessSettings.Bloom"/> and <see cref="PixelPostProcessSettings.Water"/>. Nothing here costs
    /// anything until something asks for temporal rendering. The thresholds decide when a camera move is a cut: a frame
    /// whose camera moved further than <see cref="CutDistanceMetres"/> or turned more than <see cref="CutAngleDegrees"/>
    /// since the last frame drops its temporal history, exactly as an explicit <see cref="Scene3D.CameraCut"/> does. The
    /// two cuts differ only in the reason they report: <see cref="TemporalResetReason.CameraCutDetected"/> for this one
    /// and <see cref="TemporalResetReason.CameraCutRequested"/> for the explicit call.
    /// </summary>
    public sealed class TemporalSettings
    {
        /// <summary>The largest distance, in metres, the camera eye may move between two frames and still continue the
        /// previous one. Default 16. <see cref="float.PositiveInfinity"/> turns the distance check off. A render origin
        /// step of more than one 128 m cell is still a cut, see <see cref="TemporalResetReason.CameraCutDetected"/>.</summary>
        public float CutDistanceMetres = 16f;

        /// <summary>The largest angle, in degrees, the camera's forward direction may turn between two frames and still
        /// continue the previous one. Default 60. A value of 180 or more turns the angle check off.</summary>
        public float CutAngleDegrees = 60f;
    }
}
