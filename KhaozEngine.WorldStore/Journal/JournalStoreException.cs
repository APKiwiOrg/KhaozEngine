using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.WorldStore.Journal;

public enum JournalStoreFailureKind
{
    Unavailable,
    Timeout,
    Deadlock,
    Cancelled,
    UnknownOutcome,
    CorruptData,
    SchemaMismatch,
    ConstraintViolation,
}

public enum JournalStoreFailureCertainty
{
    DefinitelyNotCommitted,
    Unknown,
    CommittedDataUnreadable,
}

public enum JournalStoreFailureScope
{
    OperationStreams,
    WholeStore,
}

public sealed class JournalStoreException : Exception
{
    private readonly IReadOnlyList<string> streamKeys;

    public JournalStoreException(
        JournalStoreFailureKind kind,
        JournalStoreFailureCertainty certainty,
        JournalStoreFailureScope scope,
        IReadOnlyList<string>? streamKeys,
        string message,
        Exception? innerException = null)
        : this(null, kind, certainty, scope, streamKeys, message, innerException)
    {
    }

    /// <summary>Creates a refusal with its rule beside the existing failure dimensions and stream snapshot.</summary>
    public JournalStoreException(
        JournalStoreRefusal reason,
        JournalStoreFailureKind kind,
        JournalStoreFailureCertainty certainty,
        JournalStoreFailureScope scope,
        IReadOnlyList<string>? streamKeys,
        string message,
        Exception? innerException = null)
        : this((JournalStoreRefusal?)reason, kind, certainty, scope, streamKeys, message, innerException)
    {
    }

    private JournalStoreException(
        JournalStoreRefusal? reason,
        JournalStoreFailureKind kind,
        JournalStoreFailureCertainty certainty,
        JournalStoreFailureScope scope,
        IReadOnlyList<string>? streamKeys,
        string message,
        Exception? innerException)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(message);
        string[] copy = CopyStreamKeys(streamKeys);
        if (scope == JournalStoreFailureScope.WholeStore && copy.Length != 0)
            throw new ArgumentException("A whole-store failure cannot name operation streams.", nameof(streamKeys));
        if (scope == JournalStoreFailureScope.OperationStreams && copy.Length == 0)
            throw new ArgumentException("An operation-stream failure must name at least one stream.", nameof(streamKeys));
        Reason = reason;
        Kind = kind;
        Certainty = certainty;
        Scope = scope;
        this.streamKeys = ImmutableArray.Create(copy);
    }

    /// <summary>The exact rule refused, or null for an instance consumer code built through the legacy constructor.</summary>
    public JournalStoreRefusal? Reason { get; }

    /// <summary>The affected stream for a single-stream checksum, sequence or projection-limit refusal.</summary>
    public string? StreamKey { get; init; }
    /// <summary>The stored event version whose checksum failed.</summary>
    public long? StreamVersion { get; init; }
    /// <summary>The stored projection section whose checksum failed.</summary>
    public string? SectionName { get; init; }
    /// <summary>The operation whose stored result checksum failed.</summary>
    public Guid? OperationId { get; init; }
    /// <summary>The next expected event version at a gap, or the compared schema version.</summary>
    public long? ExpectedVersion { get; init; }
    /// <summary>The event version found at a gap, negative stored stream head or unsupported schema version.</summary>
    public long? ActualVersion { get; init; }
    /// <summary>The captured through version an incomplete event page should have reached.</summary>
    public long? ExpectedThroughVersion { get; init; }
    /// <summary>The last version an incomplete event page actually reached, or its after version when empty.</summary>
    public long? ActualThroughVersion { get; init; }
    /// <summary>The resulting per-stream projection bytes refused by a replacement.</summary>
    public long? ActualBytes { get; init; }
    /// <summary>The inclusive configured per-stream projection byte limit.</summary>
    public long? MaximumBytes { get; init; }
    /// <summary>The resulting per-stream projection section count refused by a replacement.</summary>
    public long? ActualCount { get; init; }
    /// <summary>The inclusive configured per-stream projection section limit.</summary>
    public long? MaximumCount { get; init; }
    /// <summary>The zero-based stored row index, or the latter row of an invalid ordered pair.</summary>
    public int? Index { get; init; }
    /// <summary>The stored-row constructor parameter whose stream key is invalid.</summary>
    public string? ParameterName { get; init; }
    /// <summary>The stored-row constructor timestamp parameter whose offset is not zero.</summary>
    public string? FieldName { get; init; }

    /// <summary>The migration required by a schema refusal.</summary>
    public string? RequiredMigration { get; init; }
    /// <summary>The compared SQLite object qualified by kind, or the unreadable SQL Server definition name.</summary>
    public string? ObjectName { get; init; }
    /// <summary>The CLR type of a malformed non-null schema version value.</summary>
    public Type? MetadataType { get; init; }
    /// <summary>The unsupported connection journal mode.</summary>
    public string? ActualMode { get; init; }
    /// <summary>The required persistent writer journal mode.</summary>
    public string? RequiredMode { get; init; }

    /// <summary>The permission required to expose an unreadable SQL Server object definition.</summary>
    public string? RequiredPermission { get; init; }
    /// <summary>The SQL Server application lock's negative return code.</summary>
    public int? ReturnCode { get; init; }
    /// <summary>The provider error number for a schema provider failure.</summary>
    public int? ProviderCode { get; init; }
    /// <summary>The schema stage refused by cancellation or a provider failure.</summary>
    public JournalOperationStage? Stage { get; init; }

    private readonly IReadOnlyList<long>? supportedVersions;
    /// <summary>The schema versions supported by the refusing mode, as an owned snapshot.</summary>
    public IReadOnlyList<long>? SupportedVersions
    {
        get => supportedVersions;
        init => supportedVersions = value is null ? null : ImmutableArray.Create(value.ToArray());
    }

    private readonly IReadOnlyList<string>? missingObjects;
    /// <summary>The missing schema objects in ordinal order, as an owned snapshot.</summary>
    public IReadOnlyList<string>? MissingObjects
    {
        get => missingObjects;
        init => missingObjects = value is null ? null : ImmutableArray.Create(value.ToArray());
    }

    private readonly IReadOnlyList<string>? unexpectedObjects;
    /// <summary>The unexpected schema objects in ordinal order, as an owned snapshot.</summary>
    public IReadOnlyList<string>? UnexpectedObjects
    {
        get => unexpectedObjects;
        init => unexpectedObjects = value is null ? null : ImmutableArray.Create(value.ToArray());
    }

    private readonly IReadOnlyList<JournalHostObjectFinding>? hostObjects;
    /// <summary>Every host key and trigger a reset would fire, in provider order, as an owned snapshot.</summary>
    public IReadOnlyList<JournalHostObjectFinding>? HostObjects
    {
        get => hostObjects;
        init => hostObjects = value is null ? null : ImmutableArray.Create(value.ToArray());
    }

    public JournalStoreFailureKind Kind { get; }
    public JournalStoreFailureCertainty Certainty { get; }
    public JournalStoreFailureScope Scope { get; }
    public IReadOnlyList<string> StreamKeys => streamKeys;

    private static string[] CopyStreamKeys(IReadOnlyList<string>? values)
    {
        if (values is null) return Array.Empty<string>();
        if (values.Count > JournalLimits.EngineMaximumStreamsPerOperation)
            throw new ArgumentOutOfRangeException(nameof(values), values.Count, $"Collection cannot exceed {JournalLimits.EngineMaximumStreamsPerOperation} items.");
        var copy = new string[values.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = JournalValidation.StreamKey(values[i], nameof(values));
        Array.Sort(copy, StringComparer.Ordinal);
        for (int i = 1; i < copy.Length; i++)
            if (StringComparer.Ordinal.Equals(copy[i - 1], copy[i])) throw new ArgumentException("Stream keys must be unique.", nameof(values));
        return copy;
    }
}
