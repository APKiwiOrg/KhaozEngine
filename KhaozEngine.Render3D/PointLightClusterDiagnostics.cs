namespace KhaozEngine.Render3D;

/// <summary>The camera projection used to build the most recent point-light cluster grid.</summary>
public enum PointLightClusterProjection
{
    Invalid,
    Orthographic,
    Perspective,
}

/// <summary>Observability for the most recently built point-light cluster grid.</summary>
public readonly record struct PointLightClusterDiagnostics(
    int SubmittedLightCount,
    int ClusterCount,
    int LightReferenceCount,
    int OverflowedClusterCount,
    PointLightClusterProjection Projection,
    float NearDepth,
    float FarDepth)
{
    /// <summary>Whether the grid has valid camera geometry.</summary>
    public bool IsValid => Projection != PointLightClusterProjection.Invalid;

    /// <summary>Whether every fragment must walk the complete submitted light list.</summary>
    public bool UsesFullFallback => SubmittedLightCount > 0 && !IsValid;

    /// <summary>Whether any fragment may need the complete-list fallback.</summary>
    public bool HasFallbackClusters => UsesFullFallback || OverflowedClusterCount > 0;
}
