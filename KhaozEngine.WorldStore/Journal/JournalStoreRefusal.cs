namespace KhaozEngine.WorldStore.Journal;

/// <summary>The journal rule refused, independent of failure kind, commit certainty and scope.</summary>
public enum JournalStoreRefusal
{
    /// <summary>A stored snapshot's checksum does not match its data.</summary>
    SnapshotChecksumMismatch,
    /// <summary>A stored event's checksum does not match its payload.</summary>
    EventChecksumMismatch,
    /// <summary>A stored projection section's checksum does not match its data.</summary>
    ProjectionChecksumMismatch,
    /// <summary>A stored operation result's checksum does not match its data.</summary>
    ResultChecksumMismatch,
    /// <summary>A stored event does not have the next expected stream version.</summary>
    EventSequenceGap,
    /// <summary>An unbounded-by-count-or-bytes page ended before its captured through version.</summary>
    EventPageIncomplete,
    /// <summary>Projection replacement would exceed the configured per-stream section count.</summary>
    ProjectionSectionCountExceeded,
    /// <summary>Projection replacement would exceed the configured per-stream aggregate bytes.</summary>
    ProjectionByteCountExceeded,
    /// <summary>A stored stream row has an invalid key.</summary>
    StoredStreamKeyInvalid,
    /// <summary>A stored stream row has a negative head version.</summary>
    StoredHeadVersionNegative,
    /// <summary>A stored stream row has a timestamp whose offset is not zero.</summary>
    StoredTimestampNonUtc,
    /// <summary>The kept stored stream rows are not unique and in ordinal key order.</summary>
    StoredStreamOrderInvalid,
}
