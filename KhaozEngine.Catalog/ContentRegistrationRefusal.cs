namespace KhaozEngine.Catalog;

/// <summary>The rule that refused a content type registration or snapshot admission.</summary>
public enum ContentRegistrationRefusal
{
    /// <summary>The registry was frozen before this registration.</summary>
    RegistryFrozen,
    /// <summary>The type key is empty.</summary>
    TypeKeyEmpty,
    /// <summary>Type id zero is reserved.</summary>
    TypeIdReserved,
    /// <summary>The registration band is not a recognized band.</summary>
    BandUnrecognized,
    /// <summary>The type id is outside the caller's registration band.</summary>
    BandTypeIdOutsideRange,
    /// <summary>Another registration already owns this type id.</summary>
    TypeIdDuplicate,
    /// <summary>Another registration already owns this type key.</summary>
    TypeKeyDuplicate,
    /// <summary>The chunk slot count is outside the inclusive range 256 to 65,536.</summary>
    ChunkSlotsOutsideRange,
    /// <summary>The in-range chunk slot count is not a power of two.</summary>
    ChunkSlotsNotPowerOfTwo,
    /// <summary>The row cap is outside the inclusive byte range 1 to 4,096.</summary>
    RowBytesOutsideRange,
    /// <summary>The chunk slots and row cap exceed the 16 MiB uncompressed chunk ceiling.</summary>
    ChunkBytesExceeded,
    /// <summary>The codec supplied no written field list.</summary>
    CodecWrittenFieldsMissing,
    /// <summary>The codec writes schema fields that are derived markers carrying no bytes.</summary>
    CodecWritesDerivedMarkers,
    /// <summary>The codec's written fields differ from the schema's non-marker fields.</summary>
    CodecSchemaFieldsMismatch,
    /// <summary>A row's type is unregistered in the snapshot builder's registry.</summary>
    SnapshotTypeUnregistered,
}
