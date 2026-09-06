using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KhaozEngine.WorldStore.Journal;

public sealed class JournalSnapshot
{
    private readonly byte[] data;
    private readonly byte[] dataChecksum;

    public JournalSnapshot(string streamKey, long throughVersion, string snapshotSchema, int snapshotSchemaVersion, byte[] data, DateTimeOffset createdAtUtc)
        : this(streamKey, throughVersion, snapshotSchema, snapshotSchemaVersion, data, JournalValidation.Hash(data), createdAtUtc)
    {
    }

    public JournalSnapshot(string streamKey, long throughVersion, string snapshotSchema, int snapshotSchemaVersion, byte[] data, byte[] dataChecksum, DateTimeOffset createdAtUtc)
    {
        StreamKey = JournalValidation.StreamKey(streamKey);
        JournalValidation.NonNegative(throughVersion, nameof(throughVersion));
        ThroughVersion = throughVersion;
        SnapshotSchema = JournalValidation.Identity(snapshotSchema, nameof(snapshotSchema), JournalLimits.EngineMaximumIdentityCharacters);
        JournalValidation.Positive(snapshotSchemaVersion, nameof(snapshotSchemaVersion));
        SnapshotSchemaVersion = snapshotSchemaVersion;
        this.data = JournalValidation.CopyBytes(data, JournalLimits.EngineMaximumSnapshotBytes, nameof(data));
        this.dataChecksum = CopyChecksum(dataChecksum, nameof(dataChecksum));
        CreatedAtUtc = createdAtUtc;
    }

    public string StreamKey { get; }
    public long ThroughVersion { get; }
    public string SnapshotSchema { get; }
    public int SnapshotSchemaVersion { get; }
    public ReadOnlyMemory<byte> Data => JournalValidation.CopyForRead(data);
    public ReadOnlyMemory<byte> DataChecksum => JournalValidation.CopyForRead(dataChecksum);
    public DateTimeOffset CreatedAtUtc { get; }
    public bool HasValidChecksum => JournalValidation.HashMatches(data, dataChecksum);

    private static byte[] CopyChecksum(byte[] value, string parameterName)
    {
        byte[] copy = JournalValidation.CopyBytes(value, 32, parameterName);
        if (copy.Length != 32) throw new ArgumentException("Checksum must contain exactly 32 bytes.", parameterName);
        return copy;
    }
}

public sealed class JournalEventRead
{
    public JournalEventRead(string streamKey, long afterVersion, long? throughVersion, int maxEvents, int maxBytes)
    {
        StreamKey = JournalValidation.StreamKey(streamKey);
        JournalValidation.NonNegative(afterVersion, nameof(afterVersion));
        if (throughVersion is not null && throughVersion.Value < afterVersion)
            throw new ArgumentOutOfRangeException(nameof(throughVersion), throughVersion, "Through version cannot precede after version.");
        JournalValidation.Positive(maxEvents, nameof(maxEvents));
        JournalValidation.Maximum(maxEvents, JournalLimits.EngineMaximumEventsPerReadPage, nameof(maxEvents));
        JournalValidation.Positive(maxBytes, nameof(maxBytes));
        JournalValidation.Maximum(maxBytes, JournalLimits.EngineMaximumAggregateEventReadBytes, nameof(maxBytes));
        AfterVersion = afterVersion;
        ThroughVersion = throughVersion;
        MaxEvents = maxEvents;
        MaxBytes = maxBytes;
    }

    public string StreamKey { get; }
    public long AfterVersion { get; }
    public long? ThroughVersion { get; }
    public int MaxEvents { get; }
    public int MaxBytes { get; }

    public void Validate(JournalLimits? limits = null)
    {
        limits ??= JournalLimits.Maximum;
        JournalValidation.Maximum(StreamKey.Length, limits.StreamKeyCharacters, nameof(StreamKey));
        JournalValidation.Maximum(MaxEvents, limits.EventsPerReadPage, nameof(MaxEvents));
        JournalValidation.Maximum(MaxBytes, limits.AggregateEventReadBytes, nameof(MaxBytes));
    }
}

public enum JournalEventPageStatus
{
    Success,
    SnapshotRequired,
    NotFound,
}

public sealed class JournalStoredEvent
{
    private readonly byte[] payload;
    private readonly byte[] payloadChecksum;

    public JournalStoredEvent(string streamKey, long streamVersion, Guid operationId, int operationOrdinal, string eventType, int eventSchemaVersion, byte[] payload, DateTimeOffset committedAtUtc)
        : this(streamKey, streamVersion, operationId, operationOrdinal, eventType, eventSchemaVersion, payload, JournalValidation.Hash(payload), committedAtUtc)
    {
    }

    public JournalStoredEvent(string streamKey, long streamVersion, Guid operationId, int operationOrdinal, string eventType, int eventSchemaVersion, byte[] payload, byte[] payloadChecksum, DateTimeOffset committedAtUtc)
    {
        StreamKey = JournalValidation.StreamKey(streamKey);
        if (streamVersion < 1) throw new ArgumentOutOfRangeException(nameof(streamVersion), streamVersion, "Stored event version must be positive.");
        if (operationId == Guid.Empty) throw new ArgumentException("Operation ID cannot be empty.", nameof(operationId));
        if (operationOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(operationOrdinal));
        StreamVersion = streamVersion;
        OperationId = operationId;
        OperationOrdinal = operationOrdinal;
        EventType = JournalValidation.Identity(eventType, nameof(eventType), JournalLimits.EngineMaximumIdentityCharacters);
        JournalValidation.Positive(eventSchemaVersion, nameof(eventSchemaVersion));
        EventSchemaVersion = eventSchemaVersion;
        this.payload = JournalValidation.CopyBytes(payload, JournalLimits.EngineMaximumEventPayloadBytes, nameof(payload));
        this.payloadChecksum = CopyChecksum(payloadChecksum, nameof(payloadChecksum));
        CommittedAtUtc = committedAtUtc;
    }

    public string StreamKey { get; }
    public long StreamVersion { get; }
    public Guid OperationId { get; }
    public int OperationOrdinal { get; }
    public string EventType { get; }
    public int EventSchemaVersion { get; }
    public ReadOnlyMemory<byte> Payload => JournalValidation.CopyForRead(payload);
    public ReadOnlyMemory<byte> PayloadChecksum => JournalValidation.CopyForRead(payloadChecksum);
    public DateTimeOffset CommittedAtUtc { get; }
    public bool HasValidChecksum => JournalValidation.HashMatches(payload, payloadChecksum);

    private static byte[] CopyChecksum(byte[] value, string parameterName)
    {
        byte[] copy = JournalValidation.CopyBytes(value, 32, parameterName);
        if (copy.Length != 32) throw new ArgumentException("Checksum must contain exactly 32 bytes.", parameterName);
        return copy;
    }
}

public sealed class JournalEventPage
{
    private readonly ReadOnlyCollection<JournalStoredEvent> events;

    public JournalEventPage(JournalEventPageStatus status, string streamKey, long throughVersion, IReadOnlyList<JournalStoredEvent> events, bool reachedThroughVersion)
    {
        StreamKey = JournalValidation.StreamKey(streamKey);
        JournalValidation.NonNegative(throughVersion, nameof(throughVersion));
        JournalStoredEvent[] copy = JournalValidation.CopyItems(events, JournalLimits.EngineMaximumEventsPerReadPage, nameof(events));
        int returnedBytes = 0;
        long priorVersion = -1;
        foreach (JournalStoredEvent value in copy)
        {
            if (!StringComparer.Ordinal.Equals(value.StreamKey, StreamKey)) throw new ArgumentException("Every event must belong to the requested stream.", nameof(events));
            if (priorVersion >= value.StreamVersion) throw new ArgumentException("Events must be ordered by ascending stream version.", nameof(events));
            priorVersion = value.StreamVersion;
            returnedBytes = checked(returnedBytes + value.Payload.Length);
        }
        JournalValidation.Maximum(returnedBytes, JournalLimits.EngineMaximumAggregateEventReadBytes, nameof(events));
        Status = status;
        ThroughVersion = throughVersion;
        this.events = Array.AsReadOnly(copy);
        ReturnedBytes = returnedBytes;
        ReachedThroughVersion = reachedThroughVersion;
    }

    public JournalEventPageStatus Status { get; }
    public string StreamKey { get; }
    public long ThroughVersion { get; }
    public IReadOnlyList<JournalStoredEvent> Events => events;
    public long? FirstVersion => events.Count == 0 ? null : events[0].StreamVersion;
    public long? LastVersion => events.Count == 0 ? null : events[^1].StreamVersion;
    public int ReturnedBytes { get; }
    public bool ReachedThroughVersion { get; }
}

public sealed class JournalProjectionQuery
{
    public JournalProjectionQuery(string streamKey, string? cursor = null)
    {
        StreamKey = JournalValidation.StreamKey(streamKey);
        Cursor = cursor;
    }

    public string StreamKey { get; }
    public string? Cursor { get; }
}

public enum JournalProjectionReadStatus
{
    Success,
    ResetRequired,
    NotFound,
}

public sealed class JournalProjectionSection
{
    private readonly byte[] data;
    private readonly byte[] dataChecksum;

    public JournalProjectionSection(string streamKey, string sectionName, long sourceVersion, string projectionSchema, int projectionSchemaVersion, byte[] data, DateTimeOffset updatedAtUtc)
        : this(streamKey, sectionName, sourceVersion, projectionSchema, projectionSchemaVersion, data, JournalValidation.Hash(data), updatedAtUtc)
    {
    }

    public JournalProjectionSection(string streamKey, string sectionName, long sourceVersion, string projectionSchema, int projectionSchemaVersion, byte[] data, byte[] dataChecksum, DateTimeOffset updatedAtUtc)
    {
        StreamKey = JournalValidation.StreamKey(streamKey);
        SectionName = JournalValidation.Identity(sectionName, nameof(sectionName), JournalLimits.EngineMaximumIdentityCharacters);
        JournalValidation.NonNegative(sourceVersion, nameof(sourceVersion));
        SourceVersion = sourceVersion;
        ProjectionSchema = JournalValidation.Identity(projectionSchema, nameof(projectionSchema), JournalLimits.EngineMaximumIdentityCharacters);
        JournalValidation.Positive(projectionSchemaVersion, nameof(projectionSchemaVersion));
        ProjectionSchemaVersion = projectionSchemaVersion;
        this.data = JournalValidation.CopyBytes(data, JournalLimits.EngineMaximumProjectionSectionBytes, nameof(data));
        this.dataChecksum = CopyChecksum(dataChecksum, nameof(dataChecksum));
        UpdatedAtUtc = updatedAtUtc;
    }

    public string StreamKey { get; }
    public string SectionName { get; }
    public long SourceVersion { get; }
    public string ProjectionSchema { get; }
    public int ProjectionSchemaVersion { get; }
    public ReadOnlyMemory<byte> Data => JournalValidation.CopyForRead(data);
    public ReadOnlyMemory<byte> DataChecksum => JournalValidation.CopyForRead(dataChecksum);
    public DateTimeOffset UpdatedAtUtc { get; }
    public bool HasValidChecksum => JournalValidation.HashMatches(data, dataChecksum);

    private static byte[] CopyChecksum(byte[] value, string parameterName)
    {
        byte[] copy = JournalValidation.CopyBytes(value, 32, parameterName);
        if (copy.Length != 32) throw new ArgumentException("Checksum must contain exactly 32 bytes.", parameterName);
        return copy;
    }
}

public sealed class JournalProjectionRead
{
    private readonly ReadOnlyCollection<JournalProjectionSection> sections;

    public JournalProjectionRead(JournalProjectionReadStatus status, string streamKey, long headVersion, IReadOnlyList<JournalProjectionSection> sections, string? cursor)
    {
        StreamKey = JournalValidation.StreamKey(streamKey);
        JournalValidation.NonNegative(headVersion, nameof(headVersion));
        JournalProjectionSection[] copy = JournalValidation.CopyItems(sections, JournalLimits.EngineMaximumProjectionSectionsPerStream, nameof(sections));
        Array.Sort(copy, static (left, right) => StringComparer.Ordinal.Compare(left.SectionName, right.SectionName));
        int bytes = 0;
        for (int i = 0; i < copy.Length; i++)
        {
            if (!StringComparer.Ordinal.Equals(copy[i].StreamKey, StreamKey)) throw new ArgumentException("Every projection must belong to the requested stream.", nameof(sections));
            if (i > 0 && StringComparer.Ordinal.Equals(copy[i - 1].SectionName, copy[i].SectionName)) throw new ArgumentException("Projection sections must be unique.", nameof(sections));
            bytes = checked(bytes + copy[i].Data.Length);
        }
        JournalValidation.Maximum(bytes, JournalLimits.EngineMaximumAggregateProjectionBytesPerStream, nameof(sections));
        Status = status;
        HeadVersion = headVersion;
        this.sections = Array.AsReadOnly(copy);
        Cursor = cursor;
        ReturnedBytes = bytes;
    }

    public JournalProjectionReadStatus Status { get; }
    public string StreamKey { get; }
    public long HeadVersion { get; }
    public IReadOnlyList<JournalProjectionSection> Sections => sections;
    public string? Cursor { get; }
    public int ReturnedBytes { get; }
}
