namespace KhaozEngine.Render3D;

/// <summary>Water records and cluster references uploaded for the latest rendered frame.
/// Counts are zero when no queued water opts in or the camera grid is invalid.</summary>
public readonly record struct WaterVolumeClusterDiagnostics(
    int RecordCount,
    int DroppedRecords,
    int ReferenceCount,
    int OverflowedClusters);
