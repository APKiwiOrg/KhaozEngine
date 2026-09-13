using System;
using System.Collections.Generic;

namespace KhaozEngine.WorldStore.Journal;

/// <summary>One projection section held in the admitted view, either the committed baseline or an admitted write over it.</summary>
internal sealed class AdmittedSectionEntry
{
    internal AdmittedSectionEntry(
        string sectionName,
        string projectionSchema,
        int projectionSchemaVersion,
        byte[] data,
        long sourceVersion,
        long ownerSequence)
    {
        SectionName = sectionName;
        ProjectionSchema = projectionSchema;
        ProjectionSchemaVersion = projectionSchemaVersion;
        Data = data;
        SourceVersion = sourceVersion;
        OwnerSequence = ownerSequence;
    }

    internal string SectionName { get; }
    internal string ProjectionSchema { get; }
    internal int ProjectionSchemaVersion { get; }
    internal byte[] Data { get; }
    internal long SourceVersion { get; }

    /// <summary>The admission sequence of the operation that wrote it, or -1 for the committed baseline.</summary>
    internal long OwnerSequence { get; }
}

/// <summary>
/// The executor's live view of one stream: the committed baseline the consumer seeded, the ordered queue of
/// admitted operations over it, and the projection sections those operations have written but not yet committed.
/// </summary>
internal sealed class AdmittedStreamState
{
    private const long CommittedOwner = -1;
    private readonly List<AdmittedJournalOperation> queue = new();
    private readonly Dictionary<string, AdmittedSectionEntry> committed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AdmittedSectionEntry> uncommitted = new(StringComparer.Ordinal);

    internal AdmittedStreamState(string streamKey) => StreamKey = streamKey;

    internal string StreamKey { get; }
    internal bool IsSeeded { get; private set; }
    internal long CommittedVersion { get; private set; }
    internal long AdmittedHeadVersion { get; private set; }
    internal int Depth => queue.Count;
    internal bool IsDroppable => queue.Count == 0 && !IsSeeded;

    internal int UncommittedOperationCount
    {
        get
        {
            int total = 0;
            foreach (AdmittedJournalOperation operation in queue)
                if (!operation.Withdrawn) total++;
            return total;
        }
    }

    internal void Seed(long committedVersion, IReadOnlyList<JournalProjectionSection> sections)
    {
        IsSeeded = true;
        CommittedVersion = committedVersion;
        committed.Clear();
        foreach (JournalProjectionSection section in sections)
            committed[section.SectionName] = new AdmittedSectionEntry(
                section.SectionName,
                section.ProjectionSchema,
                section.ProjectionSchemaVersion,
                section.DataSpan.ToArray(),
                section.SourceVersion,
                CommittedOwner);
        Rebuild();
    }

    /// <summary>Takes the first operation's expected version as the baseline for a stream the consumer never seeded.</summary>
    internal void Adopt(long baselineVersion)
    {
        CommittedVersion = baselineVersion;
        AdmittedHeadVersion = baselineVersion;
    }

    internal long Enqueue(AdmittedJournalOperation operation, JournalStreamMutation mutation)
    {
        queue.Add(operation);
        AdmittedHeadVersion += mutation.Events.Count;
        return AdmittedHeadVersion;
    }

    internal void Write(JournalProjectionWrite write, long sourceVersion, long ownerSequence)
        => uncommitted[write.SectionName] = new AdmittedSectionEntry(
            write.SectionName,
            write.ProjectionSchema,
            write.ProjectionSchemaVersion,
            write.DataSpan.ToArray(),
            sourceVersion,
            ownerSequence);

    internal int IndexOf(AdmittedJournalOperation operation) => queue.IndexOf(operation);

    internal AdmittedJournalOperation? Head => queue.Count == 0 ? null : queue[0];

    internal AdmittedJournalOperation At(int index) => queue[index];

    internal void Remove(AdmittedJournalOperation operation) => queue.Remove(operation);

    /// <summary>Folds one acknowledged commit into the committed baseline and clears the writes it owned.</summary>
    internal void Promote(AdmittedJournalOperation operation, long committedAfterVersion)
    {
        CommittedVersion = committedAfterVersion;
        foreach (JournalProjectionWrite write in operation.Commit.ProjectionWrites)
        {
            if (!StringComparer.Ordinal.Equals(write.StreamKey, StreamKey)) continue;
            if (uncommitted.TryGetValue(write.SectionName, out AdmittedSectionEntry? current) && current.OwnerSequence == operation.Sequence)
                uncommitted.Remove(write.SectionName);
            committed[write.SectionName] = new AdmittedSectionEntry(
                write.SectionName,
                write.ProjectionSchema,
                write.ProjectionSchemaVersion,
                write.DataSpan.ToArray(),
                committedAfterVersion,
                CommittedOwner);
        }
    }

    /// <summary>Recomputes the head version and every uncommitted write from the operations still standing on this stream.</summary>
    internal void Rebuild()
    {
        uncommitted.Clear();
        AdmittedHeadVersion = CommittedVersion;
        foreach (AdmittedJournalOperation operation in queue)
        {
            if (operation.Withdrawn) continue;
            for (int i = 0; i < operation.Commit.StreamMutations.Count; i++)
            {
                JournalStreamMutation mutation = operation.Commit.StreamMutations[i];
                if (!StringComparer.Ordinal.Equals(mutation.StreamKey, StreamKey)) continue;
                AdmittedHeadVersion += mutation.Events.Count;
                operation.AdmittedAfterVersions[i] = AdmittedHeadVersion;
            }
            foreach (JournalProjectionWrite write in operation.Commit.ProjectionWrites)
                if (StringComparer.Ordinal.Equals(write.StreamKey, StreamKey))
                    Write(write, AdmittedHeadVersion, operation.Sequence);
        }
    }

    internal bool TryGetSection(string sectionName, out AdmittedSectionEntry entry, out bool isCommitted)
    {
        if (uncommitted.TryGetValue(sectionName, out AdmittedSectionEntry? admittedEntry))
        {
            entry = admittedEntry;
            isCommitted = false;
            return true;
        }
        if (committed.TryGetValue(sectionName, out AdmittedSectionEntry? committedEntry))
        {
            entry = committedEntry;
            isCommitted = true;
            return true;
        }
        entry = null!;
        isCommitted = false;
        return false;
    }

    internal JournalAdmittedSection[] SnapshotSections()
    {
        var names = new List<string>(committed.Count + uncommitted.Count);
        foreach (string name in committed.Keys) names.Add(name);
        foreach (string name in uncommitted.Keys)
            if (!committed.ContainsKey(name)) names.Add(name);
        names.Sort(StringComparer.Ordinal);

        var sections = new JournalAdmittedSection[names.Count];
        for (int i = 0; i < sections.Length; i++)
        {
            TryGetSection(names[i], out AdmittedSectionEntry entry, out bool isCommitted);
            sections[i] = ToSection(entry, isCommitted);
        }
        return sections;
    }

    internal JournalAdmittedSection ToSection(AdmittedSectionEntry entry, bool isCommitted)
        => new(StreamKey, entry.SectionName, entry.ProjectionSchema, entry.ProjectionSchemaVersion, entry.Data, entry.SourceVersion, isCommitted);
}
