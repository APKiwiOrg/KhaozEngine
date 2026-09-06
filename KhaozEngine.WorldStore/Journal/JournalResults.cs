using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KhaozEngine.WorldStore.Journal;

public enum JournalInitializeStatus
{
    Initialized,
    Replayed,
    ExistingStream,
    OperationConflict,
}

public enum JournalCommitStatus
{
    Applied,
    Replayed,
    VersionConflict,
    OperationConflict,
}

public enum JournalOperationResolutionStatus
{
    NotFound,
    Replayed,
    OperationConflict,
}

public sealed record JournalStreamVersionRange
{
    public JournalStreamVersionRange(string streamKey, long beforeVersion, long afterVersion, int eventCount)
    {
        StreamKey = JournalValidation.StreamKey(streamKey);
        JournalValidation.NonNegative(beforeVersion, nameof(beforeVersion));
        JournalValidation.NonNegative(afterVersion, nameof(afterVersion));
        if (afterVersion < beforeVersion) throw new ArgumentOutOfRangeException(nameof(afterVersion), afterVersion, "After version cannot precede before version.");
        if (eventCount < 0 || afterVersion - beforeVersion != eventCount)
            throw new ArgumentOutOfRangeException(nameof(eventCount), eventCount, "Event count must match the version range.");
        BeforeVersion = beforeVersion;
        AfterVersion = afterVersion;
        EventCount = eventCount;
    }

    public string StreamKey { get; }
    public long BeforeVersion { get; }
    public long AfterVersion { get; }
    public int EventCount { get; }
}

public sealed class JournalCommitReceipt
{
    private readonly ReadOnlyCollection<JournalStreamVersionRange> streams;
    private readonly byte[] resultData;
    private readonly byte[] resultChecksum;

    public JournalCommitReceipt(
        Guid operationId,
        DateTimeOffset committedAtUtc,
        IReadOnlyList<JournalStreamVersionRange> streams,
        string resultSchema,
        int resultSchemaVersion,
        byte[] resultData,
        bool isReplay = false)
        : this(operationId, committedAtUtc, streams, resultSchema, resultSchemaVersion, resultData, JournalValidation.Hash(resultData), isReplay)
    {
    }

    public JournalCommitReceipt(
        Guid operationId,
        DateTimeOffset committedAtUtc,
        IReadOnlyList<JournalStreamVersionRange> streams,
        string resultSchema,
        int resultSchemaVersion,
        byte[] resultData,
        byte[] resultChecksum,
        bool isReplay = false)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("Operation ID cannot be empty.", nameof(operationId));
        OperationId = operationId;
        CommittedAtUtc = committedAtUtc;
        JournalStreamVersionRange[] streamCopy = JournalValidation.CopyItems(streams, JournalLimits.EngineMaximumStreamsPerOperation, nameof(streams));
        Array.Sort(streamCopy, static (left, right) => StringComparer.Ordinal.Compare(left.StreamKey, right.StreamKey));
        for (int i = 1; i < streamCopy.Length; i++)
            if (StringComparer.Ordinal.Equals(streamCopy[i - 1].StreamKey, streamCopy[i].StreamKey))
                throw new ArgumentException($"Duplicate stream key '{streamCopy[i].StreamKey}'.", nameof(streams));
        this.streams = Array.AsReadOnly(streamCopy);
        ResultSchema = JournalValidation.Identity(resultSchema, nameof(resultSchema), JournalLimits.EngineMaximumIdentityCharacters);
        JournalValidation.Positive(resultSchemaVersion, nameof(resultSchemaVersion));
        ResultSchemaVersion = resultSchemaVersion;
        this.resultData = JournalValidation.CopyBytes(resultData, JournalLimits.EngineMaximumResultBytes, nameof(resultData));
        this.resultChecksum = JournalValidation.CopyBytes(resultChecksum, 32, nameof(resultChecksum));
        if (this.resultChecksum.Length != 32) throw new ArgumentException("Result checksum must contain exactly 32 bytes.", nameof(resultChecksum));
        IsReplay = isReplay;
    }

    public Guid OperationId { get; }
    public DateTimeOffset CommittedAtUtc { get; }
    public IReadOnlyList<JournalStreamVersionRange> Streams => streams;
    public string ResultSchema { get; }
    public int ResultSchemaVersion { get; }
    public ReadOnlyMemory<byte> ResultData => JournalValidation.CopyForRead(resultData);
    public ReadOnlyMemory<byte> ResultChecksum => JournalValidation.CopyForRead(resultChecksum);
    public bool IsReplay { get; }
    public bool HasValidResultChecksum => JournalValidation.HashMatches(resultData, resultChecksum);

    public JournalCommitReceipt AsReplay()
        => IsReplay ? this : new JournalCommitReceipt(OperationId, CommittedAtUtc, streams, ResultSchema, ResultSchemaVersion, resultData, resultChecksum, true);
}

public sealed class JournalCommitResult
{
    public JournalCommitResult(JournalCommitStatus status, JournalCommitReceipt? receipt = null)
    {
        bool carriesReceipt = status is JournalCommitStatus.Applied or JournalCommitStatus.Replayed;
        if (carriesReceipt != (receipt is not null)) throw new ArgumentException("Only applied or replayed results carry a receipt.", nameof(receipt));
        Status = status;
        Receipt = status == JournalCommitStatus.Replayed ? receipt!.AsReplay() : receipt;
    }

    public JournalCommitStatus Status { get; }
    public JournalCommitReceipt? Receipt { get; }
    public bool IsReplay => Status == JournalCommitStatus.Replayed;
}

public sealed class JournalInitializeResult
{
    public JournalInitializeResult(JournalInitializeStatus status, JournalCommitReceipt? receipt = null)
    {
        bool carriesReceipt = status is JournalInitializeStatus.Initialized or JournalInitializeStatus.Replayed;
        if (carriesReceipt != (receipt is not null)) throw new ArgumentException("Only initialized or replayed results carry a receipt.", nameof(receipt));
        Status = status;
        Receipt = status == JournalInitializeStatus.Replayed ? receipt!.AsReplay() : receipt;
    }

    public JournalInitializeStatus Status { get; }
    public JournalCommitReceipt? Receipt { get; }
    public bool IsReplay => Status == JournalInitializeStatus.Replayed;
}

public sealed class JournalOperationResolution
{
    public JournalOperationResolution(JournalOperationResolutionStatus status, JournalCommitReceipt? receipt = null)
    {
        if ((status == JournalOperationResolutionStatus.Replayed) != (receipt is not null))
            throw new ArgumentException("Only a replayed resolution carries a receipt.", nameof(receipt));
        Status = status;
        Receipt = receipt?.AsReplay();
    }

    public JournalOperationResolutionStatus Status { get; }
    public JournalCommitReceipt? Receipt { get; }
    public bool IsReplay => Status == JournalOperationResolutionStatus.Replayed;
}
