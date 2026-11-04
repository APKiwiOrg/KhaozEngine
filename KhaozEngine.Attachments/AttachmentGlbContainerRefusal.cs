namespace KhaozEngine.Attachments;

/// <summary>The reasons AttachmentGlbContainer input is refused.</summary>
public enum AttachmentGlbContainerRefusal
{
    /// <summary>Refuses an invalid GLB magic version or declared total length.</summary>
    HeaderInvalid,
    /// <summary>Refuses a truncated GLB chunk header.</summary>
    ChunkHeaderTruncated,
    /// <summary>Refuses an unaligned or out-of-bounds GLB chunk length.</summary>
    ChunkLengthInvalid,
    /// <summary>Refuses a GLB whose first chunk is not nonempty JSON.</summary>
    FirstChunkNotJson,
    /// <summary>Refuses an unexpected or duplicate post-JSON GLB chunk.</summary>
    UnexpectedChunk,
    /// <summary>Refuses a GLB JSON root that is not an object.</summary>
    JsonRootNotObject,
    /// <summary>Refuses missing or unsupported glTF asset version metadata.</summary>
    AssetVersionUnsupported,
    /// <summary>Refuses a declared buffer inconsistent with the BIN chunk.</summary>
    EmbeddedBufferLengthMismatch,
    /// <summary>Refuses more than one embedded buffer.</summary>
    MultipleEmbeddedBuffers,
    /// <summary>Refuses a GLB array property with another JSON shape.</summary>
    PropertyNotArray,
    /// <summary>Refuses a non-object entry in a GLB object array.</summary>
    ArrayEntryNotObject,
    /// <summary>Refuses a resource URI instead of embedded bytes.</summary>
    ExternalResourceUri,
    /// <summary>Refuses a duplicate property anywhere in the GLB JSON tree.</summary>
    DuplicateJsonProperty,
    /// <summary>Refuses malformed GLB JSON syntax with source context.</summary>
    JsonMalformed,
    /// <summary>Refuses a framework JSON shape or numeric conversion failure with source context.</summary>
    ValueShapeInvalid,
}
