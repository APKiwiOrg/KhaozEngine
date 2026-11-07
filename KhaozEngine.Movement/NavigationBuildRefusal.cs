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
    /// <summary>The contact navigation envelope extends outside the ordinary served scope.</summary>
    EnvelopeOutsideServedScope,
    /// <summary>The movement lease frame differs from the proof frame.</summary>
    MovementLeaseFrameMismatch,
    /// <summary>The movement lease geometry generation differs from the captured generation.</summary>
    MovementLeaseGenerationMismatch,
    /// <summary>The movement lease identity differs from the first proof lease.</summary>
    MovementLeaseIdentityChanged,
    /// <summary>The movement lease origin differs from the captured origin.</summary>
    MovementLeaseOriginMismatch,
    /// <summary>The movement lease belongs to a different physics source than the capture.</summary>
    MovementLeaseSourceMismatch,
    /// <summary>The proof could not acquire a known movement lease.</summary>
    MovementLeaseUnavailable,
    /// <summary>The context navigation frame differs from the proof frame.</summary>
    ProofFrameChanged,
    /// <summary>The movement registration generation differs from the snapshot.</summary>
    RegistrationGenerationChanged,
    /// <summary>The resting anchor query could not return known evidence.</summary>
    RestingAnchorUnavailable,
    /// <summary>The snapshot frame origin differs bitwise from the captured physics origin.</summary>
    SnapshotBasisOriginChanged,
    /// <summary>The current context frame differs bitwise from the snapshot frame.</summary>
    SnapshotContextFrameChanged,
    /// <summary>The worker lease frame differs bitwise from the snapshot frame.</summary>
    SnapshotLeaseFrameChanged,
    /// <summary>The worker lease geometry generation differs from the snapshot basis.</summary>
    SnapshotLeaseGenerationChanged,
    /// <summary>The worker lease belongs to a different physics source than the snapshot.</summary>
    SnapshotLeaseSourceChanged,
    /// <summary>The selected query view origin differs bitwise from the snapshot frame origin.</summary>
    SnapshotViewOriginChanged,
    /// <summary>The selected query view belongs to a different physics source than the snapshot.</summary>
    SnapshotViewSourceChanged,
    /// <summary>The contact support footprint cannot produce finite outward bounds.</summary>
    SupportEnvelopeUnbounded,
    /// <summary>The water extent differs bitwise from the snapshot extent.</summary>
    WaterExtentChanged,
    /// <summary>The water generation differs from the snapshot generation.</summary>
    WaterGenerationChanged,
    /// <summary>The water semantic identity differs ordinally from the snapshot identity.</summary>
    WaterIdentityChanged,
}
