namespace KhaozEngine.Attachments;

/// <summary>The reasons AttachmentAccessor input is refused.</summary>
public enum AttachmentAccessorRefusal
{
    /// <summary>Refuses an accessor with another vector or matrix shape.</summary>
    TypeMismatch,
    /// <summary>Refuses a non-boolean normalized flag.</summary>
    NormalizedNotBoolean,
    /// <summary>Refuses an unsupported accessor component or normalized encoding.</summary>
    EncodingUnsupported,
    /// <summary>Refuses an accessor with zero elements.</summary>
    CountNotPositive,
    /// <summary>Refuses a nonzero accessor offset without a buffer view.</summary>
    OffsetWithoutBufferView,
    /// <summary>Refuses an empty or oversized sparse override table.</summary>
    SparseCountOutsideAccessor,
    /// <summary>Refuses sparse indices with an unsupported component type.</summary>
    SparseIndexEncodingUnsupported,
    /// <summary>Refuses sparse indices outside the accessor or not strictly increasing.</summary>
    SparseIndexNotIncreasingInRange,
    /// <summary>Refuses compressed fit geometry in rig-v1.</summary>
    CompressionUnsupported,
    /// <summary>Refuses a view referencing another embedded buffer.</summary>
    BufferIndexNotZero,
    /// <summary>Refuses a buffer-view range outside the embedded buffer.</summary>
    BufferViewOutsideBuffer,
    /// <summary>Refuses an unsupported or misaligned accessor stride.</summary>
    ByteStrideInvalid,
    /// <summary>Refuses an accessor range or alignment outside its view.</summary>
    RangeOrAlignmentInvalid,
    /// <summary>Refuses a required accessor or container property absent from JSON.</summary>
    PropertyMissing,
    /// <summary>Refuses a required object with another JSON shape.</summary>
    ObjectRequired,
    /// <summary>Refuses a JSON index that is not a supported nonnegative integer.</summary>
    NonNegativeIntegerRequired,
    /// <summary>Refuses a missing out-of-range or non-object table entry.</summary>
    ReferenceInvalid,
}
