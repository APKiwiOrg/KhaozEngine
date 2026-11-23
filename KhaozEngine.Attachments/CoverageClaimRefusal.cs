namespace KhaozEngine.Attachments;

/// <summary>The rule that refused a coverage claim check's input. Members are only ever appended.</summary>
public enum CoverageClaimRefusal
{
    /// <summary>Refuses a body mesh list whose count differs from the region map's part count.</summary>
    BodyMeshCountMismatch,

    /// <summary>Refuses a body mesh whose triangle count differs from its region map part's.</summary>
    BodyTriangleCountMismatch,

    /// <summary>Refuses a piece covering a region the body's region map does not declare.</summary>
    RegionUnknown,

    /// <summary>Refuses a mesh whose index count is not a multiple of three.</summary>
    IndexCountNotTriangles,

    /// <summary>Refuses a mesh index that names no position of its mesh.</summary>
    IndexOutOfRange,

    /// <summary>Refuses a mesh position with a NaN or infinite component.</summary>
    PositionNotFinite,

    /// <summary>Refuses a piece mesh without a swinging vertex mask for a part that declares motion.</summary>
    SwingMaskMissing,

    /// <summary>Refuses a mesh without a part ID in a piece that declares motion.</summary>
    PieceMeshPartIdMissing,

    /// <summary>Refuses a mesh whose part ID the piece definition does not declare.</summary>
    PieceMeshPartIdUnknown,

    /// <summary>Refuses a second mesh naming the same definition part in a piece that declares motion.</summary>
    PieceMeshPartIdDuplicate,

    /// <summary>Refuses a definition part with no mesh in a piece that declares motion.</summary>
    PiecePartMeshMissing,

    /// <summary>Refuses a footprint mesh without its definition part ID.</summary>
    FootprintMeshPartIdMissing,

    /// <summary>Refuses a footprint mesh whose part ID the piece definition does not declare.</summary>
    FootprintMeshPartIdUnknown,

    /// <summary>Refuses a second footprint mesh naming the same definition part.</summary>
    FootprintMeshPartIdDuplicate,

    /// <summary>Refuses a definition part without a footprint mesh.</summary>
    FootprintPartMeshMissing,

    /// <summary>Refuses a fixed triangle sample more than 0.25 metres above the body.</summary>
    FootprintHeightAboveLimit,

    /// <summary>Refuses a triangle whose one-millimetre sampling grid exceeds the index range.</summary>
    FootprintSamplingGridTooLarge,

    /// <summary>Refuses a layered skinned part's footprint mesh whose triangle count differs from the triangles its region
    /// runs tag, identifying the part, mesh and both counts.</summary>
    FootprintMeshTriangleCountMismatch,
}

/// <summary>Which input list a refused coverage claim mesh belongs to. Members are only ever appended.</summary>
public enum CoverageClaimMeshKind
{
    /// <summary>A body mesh, indexed in the region map's part order.</summary>
    Body,

    /// <summary>A piece mesh, indexed in the caller's piece mesh order.</summary>
    Piece,
}
