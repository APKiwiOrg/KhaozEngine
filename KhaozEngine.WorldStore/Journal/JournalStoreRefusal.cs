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
    /// <summary>A stream exceeds its applicable projection section count limit.</summary>
    ProjectionSectionCountExceeded,
    /// <summary>A stream exceeds its applicable aggregate projection byte limit.</summary>
    ProjectionByteCountExceeded,
    /// <summary>A stored stream row has an invalid key.</summary>
    StoredStreamKeyInvalid,
    /// <summary>A stored stream row has a negative head version.</summary>
    StoredHeadVersionNegative,
    /// <summary>A stored stream row has a timestamp whose offset is not zero.</summary>
    StoredTimestampNonUtc,
    /// <summary>The kept stored stream rows are not unique and in ordinal key order.</summary>
    StoredStreamOrderInvalid,

    // Schema validation and reset admission.
    /// <summary>A host foreign key's delete action would write outside the journal during reset.</summary>
    HostForeignKeyActionUnsafe,
    /// <summary>A host trigger would run on a journal data table during reset.</summary>
    HostTriggerUnsafe,
    /// <summary>The connection has foreign key enforcement disabled.</summary>
    SchemaForeignKeysDisabled,
    /// <summary>The writer connection's journal mode is neither WAL nor memory.</summary>
    SchemaJournalModeUnsupported,
    /// <summary>The schema metadata row or its version value is missing.</summary>
    SchemaMetadataMissing,
    /// <summary>The provider could not read or validate the schema metadata.</summary>
    SchemaMetadataUnreadable,
    /// <summary>No journal schema exists in a mode that cannot create it.</summary>
    SchemaMissing,
    /// <summary>A declared schema object's definition differs from the supported shape.</summary>
    SchemaObjectDefinitionMismatch,
    /// <summary>The schema object count differs from the supported inventory.</summary>
    SchemaObjectInventoryMismatch,
    /// <summary>A required schema object is missing despite a matching inventory count.</summary>
    SchemaObjectMissing,
    /// <summary>The stored schema version is not an integer.</summary>
    SchemaVersionMalformed,
    /// <summary>The declared schema version is unsupported by the refusing mode.</summary>
    SchemaVersionUnsupported,
    /// <summary>SQL Server cannot expose an unencrypted schema object definition.</summary>
    DefinitionUnavailable,
    /// <summary>The SQL Server application lock was not granted.</summary>
    ApplicationLockNotGranted,
    /// <summary>The schema version column is missing or malformed.</summary>
    SchemaMetadataShapeInvalid,
    /// <summary>The SQL Server column set differs from the supported shape.</summary>
    SchemaColumnsMismatch,
    /// <summary>The SQL Server index set differs from the supported shape.</summary>
    SchemaIndexesMismatch,
    /// <summary>The SQL Server foreign key set differs from the supported shape.</summary>
    SchemaForeignKeysMismatch,
    /// <summary>The SQL Server check constraint set differs from the supported shape.</summary>
    SchemaChecksMismatch,
    /// <summary>The SQL Server default constraint set differs from the supported shape.</summary>
    SchemaDefaultsMismatch,
    /// <summary>The SQL Server trigger set differs from the supported shape.</summary>
    SchemaTriggersMismatch,
    /// <summary>The SQL Server metadata contains an empty store epoch.</summary>
    SchemaEpochEmpty,
    /// <summary>The caller cancelled schema admission or validation.</summary>
    SchemaCancelled,
    /// <summary>The SQL Server provider failed during schema admission or validation.</summary>
    SchemaProviderFailure,
    /// <summary>A failed schema initialization could not confirm its rollback.</summary>
    SchemaRollbackUnconfirmed,

    // Runtime storage, transactions and maintenance.
    /// <summary>The provider failed a journal operation.</summary>
    ProviderOperationFailed,
    /// <summary>A commit completed or may have completed before its response failed.</summary>
    CommitOutcomeUnknown,
    /// <summary>A journal operation was cancelled before its result was returned.</summary>
    OperationCancelled,
    /// <summary>The provider did not return its UTC clock value.</summary>
    ProviderClockUnavailable,
    /// <summary>A replacement snapshot has a different through version before pruning.</summary>
    ReplacementSnapshotVersionMismatch,
    /// <summary>A reset could not acquire the SQLite write lock within its timeout.</summary>
    ResetWriteLockTimedOut,
    /// <summary>The stored epoch cannot be read as a GUID.</summary>
    StoredEpochInvalid,
    /// <summary>An epoch rotation did not update exactly one metadata row.</summary>
    MetadataUpdateNotApplied,
    /// <summary>A stream being compacted has no stored snapshot.</summary>
    StreamSnapshotMissing,
    /// <summary>A SQL Server replacement snapshot differs from the requested data before pruning.</summary>
    ReplacementSnapshotDataMismatch,
    /// <summary>A runtime transport failed with confirmed rollback or before a transaction began.</summary>
    TransportFailed,
    /// <summary>A non-provider commit failure has a confirmed rollback.</summary>
    WriteFailedAfterRollback,
    /// <summary>The SQL Server batch returned no outcome row.</summary>
    BatchOutcomeMissing,
    /// <summary>The SQL Server batch returned an undefined outcome value.</summary>
    BatchOutcomeUndefined,
    /// <summary>The SQL Server batch returned an outcome unsupported by the current operation.</summary>
    BatchOutcomeUnsupported,
    /// <summary>The SQL Server batch returned a projection limit without its complete payload.</summary>
    BatchProjectionLimitIncomplete,
    /// <summary>The SQL Server replay result omitted its operation row.</summary>
    ReplayedOperationMissing,
    /// <summary>The SQL Server replay result omitted its stream result set.</summary>
    ReplayedStreamsMissing,
    /// <summary>The SQL Server batch could not update a previously validated stream head.</summary>
    BatchStreamVanished,
}
