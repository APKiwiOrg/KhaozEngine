namespace KhaozEngine.Render3D;

/// <summary>The colour history grid requested through <see cref="TemporalSettings.HistoryDetail"/>.</summary>
public enum TemporalHistoryDetail
{
    /// <summary>The display-sized history. The default.</summary>
    Display,
    /// <summary>An additional colour pair at twice the display on each axis. Kept current by replication of the
    /// resolve result. Nothing reads this pair yet, so the presented image is unchanged.</summary>
    Fine,
}
