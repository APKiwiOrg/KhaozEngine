namespace KhaozEngine.Render3D;

/// <summary>The colour history grid requested through <see cref="TemporalSettings.HistoryDetail"/>.</summary>
public enum TemporalHistoryDetail
{
    /// <summary>The display-sized history, available as an explicit opt-out and when Fine is refused.</summary>
    Display,
    /// <summary>The default request. An additional RGBA16F colour pair at twice the display on each axis. A moving pixel reads its
    /// four reprojected texels and keeps their bounded sub-pixel deviations around its resolved colour. A still pixel,
    /// below a 128th of a display pixel a frame, and a distrusted one take Display's path and replicate their result
    /// into their four texels. A creation refusal uses Display and reports
    /// <see cref="TemporalHistoryDetailFallback.CreationFailed"/>. Admission, retries and memory are on
    /// <see cref="TemporalSettings.HistoryDetail"/> and
    /// <see cref="TemporalSettings.HistoryDetailPixelLimit"/>.</summary>
    Fine,
}
