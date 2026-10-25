namespace KhaozEngine.TileWorld.Render3D;

/// <summary>Why a complete authored-bed optical field could not be built.</summary>
public enum TileWaterOpticsFailureKind
{
    /// <summary>A required corner has no available owner, or its earlier-priority owner is unloaded. The
    /// actual mesh used a strict-read fallback there, so it cannot certify an optical bed.</summary>
    MissingHeightData,
    /// <summary>The certified domain exceeds supported bounds or has unaddressable tile coordinates.
    /// The complete build is refused.</summary>
    FieldTooLarge,
    /// <summary>A region whose rendered mesh supports the certified domain has no accepted or same-transaction
    /// surface snapshot, or that snapshot no longer matches its source.</summary>
    MissingSurfaceData,
    /// <summary>The supporting triangles leave part of a certified rectangle uncovered, including a hole.</summary>
    IncompleteSurfaceCoverage,
    /// <summary>Distinct supporting triangles disagree where their projections overlap.</summary>
    AmbiguousSurface,
    /// <summary>Every bounded packing attempt exceeded 32 candidates per bin or the 1024 squared atlas.</summary>
    SurfaceCapacityExceeded,
    /// <summary>Source conversion plus field reconstruction exceeds <c>min(0.0001 metres, TileSize / 1024)</c>,
    /// or a scalar cannot be represented by every backend.</summary>
    UnrepresentableSurface,
    /// <summary>The effective water look has no certified displacement envelope, such as FFT water. Water is
    /// withdrawn while terrain still builds and publishes. A certifiable look republishes it.</summary>
    UncertifiedDisplacement,
}

/// <summary>A refused field request, with the unavailable region when source data is missing. Domain is the
/// tile-space extent of the refused level's certified rectangles. Bin axes are those of the last packing attempt,
/// zero when packing was not reached.</summary>
public readonly record struct TileWaterOpticsFailure(RegionCoord Region, int Plane, float ReferenceSurfaceY,
    TileWaterOpticsFailureKind Kind, RegionCoord? MissingRegion = null, TileRect? Domain = null,
    int BinColumns = 0, int BinRows = 0);

/// <summary>Retained optical source-snapshot bytes. Resident counts accepted surfaces of loaded regions. Pending
/// counts completed worker surfaces awaiting their frame-thread publication. Owned array storage only.</summary>
public readonly record struct TileWaterOpticsSourceBytes(long Resident, long Pending);
