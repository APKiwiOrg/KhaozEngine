using System;

namespace KhaozEngine.WorldStore.Journal;

public sealed class JournalCompaction
{
    private readonly byte[] snapshotData;
    private readonly byte[] snapshotChecksum;

    public JournalCompaction(string streamKey, long throughVersion, string snapshotSchema, int snapshotSchemaVersion, byte[] snapshotData, long? pruneThroughVersion)
    {
        StreamKey = JournalValidation.StreamKey(streamKey);
        JournalValidation.NonNegative(throughVersion, nameof(throughVersion));
        if (pruneThroughVersion is not null)
        {
            JournalValidation.NonNegative(pruneThroughVersion.Value, nameof(pruneThroughVersion));
            if (pruneThroughVersion.Value > throughVersion)
                throw new ArgumentOutOfRangeException(nameof(pruneThroughVersion), pruneThroughVersion, "Prune version cannot exceed snapshot version.");
        }
        ThroughVersion = throughVersion;
        SnapshotSchema = JournalValidation.Identity(snapshotSchema, nameof(snapshotSchema), JournalLimits.EngineMaximumIdentityCharacters);
        JournalValidation.Positive(snapshotSchemaVersion, nameof(snapshotSchemaVersion));
        SnapshotSchemaVersion = snapshotSchemaVersion;
        this.snapshotData = JournalValidation.CopyBytes(snapshotData, JournalLimits.EngineMaximumSnapshotBytes, nameof(snapshotData));
        snapshotChecksum = JournalValidation.Hash(this.snapshotData);
        PruneThroughVersion = pruneThroughVersion;
    }

    public string StreamKey { get; }
    public long ThroughVersion { get; }
    public string SnapshotSchema { get; }
    public int SnapshotSchemaVersion { get; }
    public ReadOnlyMemory<byte> SnapshotData => snapshotData;
    public ReadOnlyMemory<byte> SnapshotChecksum => snapshotChecksum;
    public long? PruneThroughVersion { get; }
    public int OwnedByteCount => snapshotData.Length;

    public void Validate(JournalLimits? limits = null)
    {
        limits ??= JournalLimits.Maximum;
        JournalValidation.Maximum(StreamKey.Length, limits.StreamKeyCharacters, nameof(StreamKey));
        JournalValidation.Maximum(SnapshotSchema.Length, limits.IdentityCharacters, nameof(SnapshotSchema));
        JournalValidation.Maximum(snapshotData.Length, limits.SnapshotBytes, nameof(SnapshotData));
    }
}

public enum JournalCompactionStatus
{
    Compacted,
    NotFound,
    VersionConflict,
}

public sealed class JournalCompactionResult
{
    public JournalCompactionResult(JournalCompactionStatus status, long previousSnapshotVersion, long snapshotVersion, int prunedEventCount)
    {
        JournalValidation.NonNegative(previousSnapshotVersion, nameof(previousSnapshotVersion));
        JournalValidation.NonNegative(snapshotVersion, nameof(snapshotVersion));
        if (prunedEventCount < 0) throw new ArgumentOutOfRangeException(nameof(prunedEventCount));
        Status = status;
        PreviousSnapshotVersion = previousSnapshotVersion;
        SnapshotVersion = snapshotVersion;
        PrunedEventCount = prunedEventCount;
    }

    public JournalCompactionStatus Status { get; }
    public long PreviousSnapshotVersion { get; }
    public long SnapshotVersion { get; }
    public int PrunedEventCount { get; }
}

public sealed class JournalOperationPurge
{
    public JournalOperationPurge(DateTimeOffset cutoffUtc, int maxOperations)
    {
        JournalValidation.Positive(maxOperations, nameof(maxOperations));
        CutoffUtc = cutoffUtc;
        MaxOperations = maxOperations;
    }

    public DateTimeOffset CutoffUtc { get; }
    public int MaxOperations { get; }
}

public sealed class JournalOperationPurgeResult
{
    public JournalOperationPurgeResult(int scannedCount, int deletedCount, int ineligibleCount, DateTimeOffset? oldestRetainedAtUtc)
    {
        if (scannedCount < 0) throw new ArgumentOutOfRangeException(nameof(scannedCount));
        if (deletedCount < 0 || deletedCount > scannedCount) throw new ArgumentOutOfRangeException(nameof(deletedCount));
        if (ineligibleCount < 0) throw new ArgumentOutOfRangeException(nameof(ineligibleCount));
        ScannedCount = scannedCount;
        DeletedCount = deletedCount;
        IneligibleCount = ineligibleCount;
        OldestRetainedAtUtc = oldestRetainedAtUtc;
    }

    public int ScannedCount { get; }
    public int DeletedCount { get; }
    public int IneligibleCount { get; }
    public DateTimeOffset? OldestRetainedAtUtc { get; }
}
