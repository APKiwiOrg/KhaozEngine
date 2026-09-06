using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KhaozEngine.WorldStore.Journal;

public sealed class JournalInitialization
{
    private readonly byte[] snapshotData;
    private readonly byte[] snapshotChecksum;
    private readonly ReadOnlyCollection<JournalProjectionWrite> projectionWrites;
    private readonly byte[] resultData;
    private readonly byte[] resultChecksum;

    public JournalInitialization(
        JournalOperationIdentity identity,
        string absentStreamKey,
        string snapshotSchema,
        int snapshotSchemaVersion,
        byte[] snapshotData,
        IReadOnlyList<JournalProjectionWrite> projectionWrites,
        string resultSchema,
        int resultSchemaVersion,
        byte[] resultData)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        AbsentStreamKey = JournalValidation.StreamKey(absentStreamKey, nameof(absentStreamKey));
        SnapshotSchema = JournalValidation.Identity(snapshotSchema, nameof(snapshotSchema), JournalLimits.EngineMaximumIdentityCharacters);
        JournalValidation.Positive(snapshotSchemaVersion, nameof(snapshotSchemaVersion));
        SnapshotSchemaVersion = snapshotSchemaVersion;
        this.snapshotData = JournalValidation.CopyBytes(snapshotData, JournalLimits.EngineMaximumSnapshotBytes, nameof(snapshotData));
        snapshotChecksum = JournalValidation.Hash(this.snapshotData);

        JournalProjectionWrite[] projections = JournalValidation.CopyItems(projectionWrites, JournalLimits.EngineMaximumProjectionWritesPerOperation, nameof(projectionWrites));
        Array.Sort(projections, static (left, right) => StringComparer.Ordinal.Compare(left.SectionName, right.SectionName));
        for (int i = 0; i < projections.Length; i++)
        {
            if (!StringComparer.Ordinal.Equals(projections[i].StreamKey, AbsentStreamKey))
                throw new ArgumentException("Initialization projections must target the absent stream.", nameof(projectionWrites));
            if (i > 0 && StringComparer.Ordinal.Equals(projections[i - 1].SectionName, projections[i].SectionName))
                throw new ArgumentException($"Duplicate projection section '{projections[i].SectionName}'.", nameof(projectionWrites));
        }
        this.projectionWrites = Array.AsReadOnly(projections);

        ResultSchema = JournalValidation.Identity(resultSchema, nameof(resultSchema), JournalLimits.EngineMaximumIdentityCharacters);
        JournalValidation.Positive(resultSchemaVersion, nameof(resultSchemaVersion));
        ResultSchemaVersion = resultSchemaVersion;
        this.resultData = JournalValidation.CopyBytes(resultData, JournalLimits.EngineMaximumResultBytes, nameof(resultData));
        resultChecksum = JournalValidation.Hash(this.resultData);
        Validate();
    }

    public JournalOperationIdentity Identity { get; }
    public string AbsentStreamKey { get; }
    public string SnapshotSchema { get; }
    public int SnapshotSchemaVersion { get; }
    public ReadOnlyMemory<byte> SnapshotData => JournalValidation.CopyForRead(snapshotData);
    public ReadOnlyMemory<byte> SnapshotChecksum => JournalValidation.CopyForRead(snapshotChecksum);
    public IReadOnlyList<JournalProjectionWrite> ProjectionWrites => projectionWrites;
    public string ResultSchema { get; }
    public int ResultSchemaVersion { get; }
    public ReadOnlyMemory<byte> ResultData => JournalValidation.CopyForRead(resultData);
    public ReadOnlyMemory<byte> ResultChecksum => JournalValidation.CopyForRead(resultChecksum);
    public int OwnedByteCount
    {
        get
        {
            int total = checked(Identity.OwnedByteCount + snapshotData.Length + resultData.Length);
            foreach (JournalProjectionWrite projection in projectionWrites) total = checked(total + projection.OwnedByteCount);
            return total;
        }
    }

    public void Validate(JournalLimits? limits = null)
    {
        limits ??= JournalLimits.Maximum;
        Identity.Validate(limits);
        JournalValidation.Maximum(AbsentStreamKey.Length, limits.StreamKeyCharacters, nameof(AbsentStreamKey));
        JournalValidation.Maximum(SnapshotSchema.Length, limits.IdentityCharacters, nameof(SnapshotSchema));
        JournalValidation.Maximum(snapshotData.Length, limits.SnapshotBytes, nameof(SnapshotData));
        JournalValidation.Maximum(projectionWrites.Count, limits.ProjectionWritesPerOperation, nameof(ProjectionWrites));
        JournalValidation.Maximum(projectionWrites.Count, limits.ProjectionSectionsPerStream, nameof(ProjectionWrites));
        JournalValidation.Maximum(ResultSchema.Length, limits.IdentityCharacters, nameof(ResultSchema));
        JournalValidation.Maximum(resultData.Length, limits.ResultBytes, nameof(ResultData));

        int projectionBytes = 0;
        foreach (JournalProjectionWrite projection in projectionWrites)
        {
            projection.Validate(limits);
            projectionBytes = checked(projectionBytes + projection.OwnedByteCount);
        }
        JournalValidation.Maximum(projectionBytes, limits.AggregateProjectionBytesPerStream, nameof(ProjectionWrites));
    }
}
