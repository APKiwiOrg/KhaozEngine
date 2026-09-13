using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KhaozEngine.WorldStore.Journal;

/// <summary>Identifies one projection section of one stream.</summary>
public sealed record JournalProjectionSectionKey
{
    public JournalProjectionSectionKey(string streamKey, string sectionName)
    {
        StreamKey = JournalValidation.StreamKey(streamKey);
        SectionName = JournalValidation.Identity(sectionName, nameof(sectionName), JournalLimits.EngineMaximumIdentityCharacters);
    }

    public string StreamKey { get; }
    public string SectionName { get; }
}

/// <summary>The admitted head version one accepted operation produced on one stream.</summary>
public sealed record JournalAdmittedStreamHead
{
    public JournalAdmittedStreamHead(string streamKey, long admittedHeadVersion)
    {
        StreamKey = JournalValidation.StreamKey(streamKey);
        JournalValidation.NonNegative(admittedHeadVersion, nameof(admittedHeadVersion));
        AdmittedHeadVersion = admittedHeadVersion;
    }

    public string StreamKey { get; }
    public long AdmittedHeadVersion { get; }
}

/// <summary>
/// One projection section as the executor currently sees it: the committed bytes when nothing is in flight over
/// them, otherwise the bytes the newest admitted uncommitted operation wrote.
/// </summary>
public sealed class JournalAdmittedSection
{
    private readonly byte[] data;

    internal JournalAdmittedSection(
        string streamKey,
        string sectionName,
        string projectionSchema,
        int projectionSchemaVersion,
        byte[] data,
        long sourceVersion,
        bool isCommitted)
    {
        StreamKey = streamKey;
        SectionName = sectionName;
        ProjectionSchema = projectionSchema;
        ProjectionSchemaVersion = projectionSchemaVersion;
        this.data = data;
        SourceVersion = sourceVersion;
        IsCommitted = isCommitted;
    }

    public string StreamKey { get; }
    public string SectionName { get; }
    public string ProjectionSchema { get; }
    public int ProjectionSchemaVersion { get; }
    public ReadOnlyMemory<byte> Data => JournalValidation.CopyForRead(data);
    public long SourceVersion { get; }

    /// <summary>True when these bytes are the acknowledged committed baseline, false when an admitted uncommitted operation wrote them.</summary>
    public bool IsCommitted { get; }
}

/// <summary>The whole admitted view of one stream, committed baseline plus every admitted uncommitted write over it.</summary>
public sealed class JournalAdmittedStream
{
    private readonly ReadOnlyCollection<JournalAdmittedSection> sections;

    internal JournalAdmittedStream(
        string streamKey,
        long committedVersion,
        long admittedHeadVersion,
        int admittedUncommittedOperations,
        JournalAdmittedSection[] sections)
    {
        StreamKey = streamKey;
        CommittedVersion = committedVersion;
        AdmittedHeadVersion = admittedHeadVersion;
        AdmittedUncommittedOperations = admittedUncommittedOperations;
        this.sections = Array.AsReadOnly(sections);
    }

    public string StreamKey { get; }

    /// <summary>The version the consumer last told the executor is durable, advanced by every acknowledged commit.</summary>
    public long CommittedVersion { get; }

    /// <summary>The version the last admitted operation on this stream will produce. Equals <see cref="CommittedVersion"/> when nothing is in flight.</summary>
    public long AdmittedHeadVersion { get; }

    public int AdmittedUncommittedOperations { get; }
    public IReadOnlyList<JournalAdmittedSection> Sections => sections;
}
