namespace KhaozEngine.Render3D;

/// <summary>The colour history grid requested through <see cref="TemporalSettings.HistoryDetail"/>.</summary>
public enum TemporalHistoryDetail
{
    /// <summary>The display-sized history. The default.</summary>
    Display,
    /// <summary>An additional RGBA16F colour pair at twice the display on each axis. An admitted moving pixel reads its
    /// four reprojected texels and keeps their bounded sub-pixel deviations around its resolved colour. A still pixel,
    /// below a 128th of a display pixel a frame, and a distrusted one take Display's path and replicate their result
    /// into their four texels. Admission, fallback and memory are on <see cref="TemporalSettings.HistoryDetail"/> and
    /// <see cref="TemporalSettings.HistoryDetailPixelLimit"/>.</summary>
    Fine,
}
