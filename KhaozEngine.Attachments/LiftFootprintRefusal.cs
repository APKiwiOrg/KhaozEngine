namespace KhaozEngine.Attachments;

/// <summary>The lift footprint admission rule that refused an input.</summary>
public enum LiftFootprintRefusal
{
    /// <summary>The body region map digest must be 64 lowercase hexadecimal characters.</summary>
    BodyRegionMapSha256Invalid,

    /// <summary>The body triangle count must be positive and leave room for all cell keys in Int32.</summary>
    BodyTriangleCountOutsideRange,

    /// <summary>Each part position must contain a part.</summary>
    PartMissing,

    /// <summary>Part IDs must be positive.</summary>
    PartIdZero,

    /// <summary>Part IDs must increase strictly.</summary>
    PartIdsNotAscending,

    /// <summary>A swinging triangle must carry only cells.</summary>
    SwingingTriangleHasHeights,

    /// <summary>A fixed triangle must carry one height per cell.</summary>
    FixedTriangleHeightCountMismatch,

    /// <summary>A cell key must be nonnegative.</summary>
    CellKeyNegative,

    /// <summary>Cell keys within a triangle must increase strictly.</summary>
    CellKeysNotAscending,

    /// <summary>A cell key must belong to one of the declared body triangles.</summary>
    CellKeyOutsideBody,

    /// <summary>A measured height must be finite and between zero and 0.25 metres inclusive.</summary>
    HeightMetresOutsideRange,

    /// <summary>The file must begin with the KHLF signature.</summary>
    MagicInvalid,

    /// <summary>The file must use the supported format version.</summary>
    FormatVersionUnsupported,

    /// <summary>The file must use the supported cell level.</summary>
    LevelUnsupported,

    /// <summary>The file's reserved byte must be zero.</summary>
    ReservedByteNonzero,

    /// <summary>The declared parts must fit in the bytes left in the file.</summary>
    PartCountExceedsRemainingBytes,

    /// <summary>The declared triangles must fit in the bytes available to the part.</summary>
    TriangleCountExceedsRemainingBytes,

    /// <summary>The declared cells and heights must fit in the bytes available to the triangle.</summary>
    EntryCountExceedsRemainingBytes,

    /// <summary>Every field must be present in full.</summary>
    Truncated,

    /// <summary>An unsigned variable-length integer must fit in 32 bits and end within five bytes.</summary>
    VarintOverflow,

    /// <summary>An unsigned variable-length integer must omit redundant leading bits in its last group.</summary>
    VarintNonCanonical,

    /// <summary>A stored height must be at most 25,000 units.</summary>
    HeightUnitsOutsideRange,

    /// <summary>The file must end after the last part.</summary>
    TrailingBytes,

    /// <summary>The footprint lists different part IDs from the set definition that names it.</summary>
    PartIdsMismatch,
}
