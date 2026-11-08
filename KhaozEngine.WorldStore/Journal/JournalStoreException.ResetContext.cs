namespace KhaozEngine.WorldStore.Journal;

public sealed partial class JournalStoreException
{
    // Reset adds prose context to the same refusal and retains every identifier and failure dimension.
    internal JournalStoreException WithResetContext(string message)
        => new(Reason, Kind, Certainty, Scope, StreamKeys, message, this)
        {
            StreamKey = StreamKey,
            StreamVersion = StreamVersion,
            SectionName = SectionName,
            OperationId = OperationId,
            ExpectedVersion = ExpectedVersion,
            ActualVersion = ActualVersion,
            ExpectedThroughVersion = ExpectedThroughVersion,
            ActualThroughVersion = ActualThroughVersion,
            ActualBytes = ActualBytes,
            MaximumBytes = MaximumBytes,
            ActualCount = ActualCount,
            MaximumCount = MaximumCount,
            Index = Index,
            ParameterName = ParameterName,
            FieldName = FieldName,
            RequiredMigration = RequiredMigration,
            ObjectName = ObjectName,
            MetadataType = MetadataType,
            ActualMode = ActualMode,
            RequiredMode = RequiredMode,
            RequiredPermission = RequiredPermission,
            ReturnCode = ReturnCode,
            ProviderCode = ProviderCode,
            Stage = Stage,
            ProviderName = ProviderName,
            LockTimeout = LockTimeout,
            SupportedVersions = SupportedVersions,
            MissingObjects = MissingObjects,
            UnexpectedObjects = UnexpectedObjects,
            HostObjects = HostObjects,
        };
}
