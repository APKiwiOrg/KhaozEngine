namespace KhaozEngine.Movement;

/// <summary>The capture or stable read rule that refused navigation construction.</summary>
public enum NavigationBuildRefusal
{
    /// <summary>The physics geometry generation differs from the captured generation.</summary>
    CaptureGeometryChanged,
    /// <summary>Contact navigation requires a capture from a query lease source.</summary>
    CaptureLeaseRequired,
    /// <summary>The physics origin differs from the captured origin.</summary>
    CaptureOriginChanged,
    /// <summary>A sampled column contains more surfaces than its configured cap.</summary>
    ColumnSurfaceCapExceeded,
    /// <summary>The stable legacy lease geometry generation differs from the capture.</summary>
    LegacyLeaseGenerationChanged,
    /// <summary>The stable legacy lease origin differs from the capture.</summary>
    LegacyLeaseOriginChanged,
    /// <summary>The stable legacy lease belongs to another physics owner.</summary>
    LegacyLeaseSourceChanged,
    /// <summary>The selected legacy query view origin differs from the capture.</summary>
    LegacyViewOriginChanged,
    /// <summary>The selected legacy query view belongs to another physics owner.</summary>
    LegacyViewSourceChanged,
    /// <summary>The stable legacy physics owner origin differs from the capture.</summary>
    LegacyWorldOriginChanged,
    /// <summary>The physics origin contains a nonfinite coordinate.</summary>
    PhysicsOriginNonfinite,
    /// <summary>Authored navigation refuses live runtime statics on its movement registration.</summary>
    RuntimeStaticsPresent,
    /// <summary>A captured surface headroom is NaN.</summary>
    SurfaceHeadroomNaN,
    /// <summary>A captured surface headroom is negative.</summary>
    SurfaceHeadroomNegative,
    /// <summary>A captured absolute surface height is not finite.</summary>
    SurfaceHeightNonfinite,
    /// <summary>A captured absolute surface height does not exceed the previous height.</summary>
    SurfaceHeightNotAscending,
}
