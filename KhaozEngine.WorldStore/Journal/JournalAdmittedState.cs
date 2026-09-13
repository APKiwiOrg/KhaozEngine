using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.WorldStore.Journal;

/// <summary>
/// The executor's admitted state layer: one ordered queue and one live projection view per stream. Every member
/// runs under the executor's lock and touches only the operation's own streams, the head of those queues, or the
/// set an individual failure supersedes.
/// </summary>
internal sealed class JournalAdmittedState
{
    private readonly Dictionary<string, AdmittedStreamState> streams = new(StringComparer.Ordinal);
    private readonly int streamQueueDepth;

    internal JournalAdmittedState(int streamQueueDepth) => this.streamQueueDepth = streamQueueDepth;

    /// <summary>The deepest single-stream queue seen since the executor started.</summary>
    internal long PeakStreamDepth { get; private set; }

    internal void Seed(string streamKey, long committedVersion, IReadOnlyList<JournalProjectionSection> sections)
    {
        if (!streams.TryGetValue(streamKey, out AdmittedStreamState? state))
        {
            state = new AdmittedStreamState(streamKey);
            streams.Add(streamKey, state);
        }
        state.Seed(committedVersion, sections);
    }

    internal bool Forget(string streamKey)
    {
        if (!streams.TryGetValue(streamKey, out AdmittedStreamState? state)) return false;
        if (state.Depth > 0) throw new InvalidOperationException($"Stream '{streamKey}' still has admitted operations.");
        streams.Remove(streamKey);
        return true;
    }

    /// <summary>Returns the refusal for a commit that cannot be admitted, or null when it can.</summary>
    internal JournalSubmissionStatus? Refusal(JournalCommit commit)
    {
        foreach (JournalStreamMutation mutation in commit.StreamMutations)
        {
            if (!streams.TryGetValue(mutation.StreamKey, out AdmittedStreamState? state)) continue;
            if (state.Depth >= streamQueueDepth) return JournalSubmissionStatus.Backpressure;
            if (mutation.ExpectedVersion != state.AdmittedHeadVersion) return JournalSubmissionStatus.VersionConflict;
        }
        return null;
    }

    /// <summary>True when any of the commit's streams already carries an admitted operation.</summary>
    internal bool HasBusyStream(JournalCommit commit)
    {
        foreach (JournalStreamMutation mutation in commit.StreamMutations)
            if (streams.TryGetValue(mutation.StreamKey, out AdmittedStreamState? state) && state.Depth > 0) return true;
        return false;
    }

    internal void Admit(AdmittedJournalOperation operation)
    {
        for (int i = 0; i < operation.Commit.StreamMutations.Count; i++)
        {
            JournalStreamMutation mutation = operation.Commit.StreamMutations[i];
            if (!streams.TryGetValue(mutation.StreamKey, out AdmittedStreamState? state))
            {
                state = new AdmittedStreamState(mutation.StreamKey);
                state.Adopt(mutation.ExpectedVersion);
                streams.Add(mutation.StreamKey, state);
            }
            operation.AdmittedAfterVersions[i] = state.Enqueue(operation, mutation);
            if (state.Depth > PeakStreamDepth) PeakStreamDepth = state.Depth;
        }

        foreach (JournalProjectionWrite write in operation.Commit.ProjectionWrites)
        {
            AdmittedStreamState state = streams[write.StreamKey];
            state.Write(write, state.AdmittedHeadVersion, operation.Sequence);
        }
    }

    internal bool CanStart(AdmittedJournalOperation operation)
    {
        if (!operation.CanStart) return false;
        foreach (JournalStreamMutation mutation in operation.Commit.StreamMutations)
            if (!ReferenceEquals(streams[mutation.StreamKey].Head, operation)) return false;
        return true;
    }

    /// <summary>
    /// Removes an acknowledged operation from its queues, folds it into the committed baseline when it committed
    /// and the consumer handled it, and collects whatever became dispatchable behind it.
    /// </summary>
    internal void Release(AdmittedJournalOperation operation, bool promote, List<AdmittedJournalOperation> runnable)
    {
        JournalCommitReceipt? receipt = operation.Completion?.Result?.Receipt;
        for (int i = 0; i < operation.Commit.StreamMutations.Count; i++)
        {
            JournalStreamMutation mutation = operation.Commit.StreamMutations[i];
            if (!streams.TryGetValue(mutation.StreamKey, out AdmittedStreamState? state)) continue;
            state.Remove(operation);
            if (promote) state.Promote(operation, CommittedAfterVersion(receipt, mutation, operation.AdmittedAfterVersions[i]));
        }

        foreach (JournalStreamMutation mutation in operation.Commit.StreamMutations)
        {
            if (!streams.TryGetValue(mutation.StreamKey, out AdmittedStreamState? state)) continue;
            if (state.Head is AdmittedJournalOperation next && CanStart(next) && !runnable.Contains(next)) runnable.Add(next);
            if (state.IsDroppable) streams.Remove(mutation.StreamKey);
        }
    }

    /// <summary>
    /// Takes one terminally failed operation's writes back out of the view, refuses everything queued behind it on
    /// the streams it touched, and describes what the consumer has to resync.
    /// </summary>
    internal JournalCorrection Withdraw(AdmittedJournalOperation failed, List<AdmittedJournalOperation> superseded)
    {
        failed.Withdrawn = true;
        var affected = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<AdmittedJournalOperation>();
        pending.Push(failed);

        while (pending.Count > 0)
        {
            AdmittedJournalOperation operation = pending.Pop();
            foreach (JournalStreamMutation mutation in operation.Commit.StreamMutations)
            {
                if (!streams.TryGetValue(mutation.StreamKey, out AdmittedStreamState? state)) continue;
                affected.Add(mutation.StreamKey);
                int index = state.IndexOf(operation);
                if (index < 0) continue;
                for (int i = index + 1; i < state.Depth; i++)
                {
                    AdmittedJournalOperation dependant = state.At(i);
                    if (dependant.Withdrawn || dependant.Started || dependant.Completion is not null) continue;
                    dependant.Withdrawn = true;
                    superseded.Add(dependant);
                    pending.Push(dependant);
                }
            }
        }

        foreach (string streamKey in affected) streams[streamKey].Rebuild();
        superseded.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
        return BuildCorrection(failed, superseded, affected);
    }

    internal bool TryGetSection(string streamKey, string sectionName, [NotNullWhen(true)] out JournalAdmittedSection? section)
    {
        section = null;
        if (!streams.TryGetValue(streamKey, out AdmittedStreamState? state)) return false;
        if (!state.TryGetSection(sectionName, out AdmittedSectionEntry entry, out bool isCommitted)) return false;
        section = state.ToSection(entry, isCommitted);
        return true;
    }

    internal bool TryGetStream(string streamKey, [NotNullWhen(true)] out JournalAdmittedStream? stream)
    {
        stream = null;
        if (!streams.TryGetValue(streamKey, out AdmittedStreamState? state)) return false;
        stream = new JournalAdmittedStream(
            state.StreamKey,
            state.CommittedVersion,
            state.AdmittedHeadVersion,
            state.UncommittedOperationCount,
            state.SnapshotSections());
        return true;
    }

    private static JournalCorrection BuildCorrection(
        AdmittedJournalOperation failed,
        List<AdmittedJournalOperation> superseded,
        SortedSet<string> affected)
    {
        var sections = new SortedSet<(string Stream, string Section)>(SectionKeyComparer.Instance);
        CollectSections(failed, sections);
        foreach (AdmittedJournalOperation operation in superseded) CollectSections(operation, sections);

        var streamKeys = new string[affected.Count];
        affected.CopyTo(streamKeys);
        var sectionKeys = new JournalProjectionSectionKey[sections.Count];
        int index = 0;
        foreach ((string stream, string section) in sections) sectionKeys[index++] = new JournalProjectionSectionKey(stream, section);
        var supersededIds = new Guid[superseded.Count];
        for (int i = 0; i < supersededIds.Length; i++) supersededIds[i] = superseded[i].Commit.Identity.OperationId;
        return new JournalCorrection(failed.Commit.Identity.OperationId, streamKeys, sectionKeys, supersededIds);
    }

    private static void CollectSections(AdmittedJournalOperation operation, SortedSet<(string Stream, string Section)> sections)
    {
        foreach (JournalProjectionWrite write in operation.Commit.ProjectionWrites)
            sections.Add((write.StreamKey, write.SectionName));
    }

    private static long CommittedAfterVersion(JournalCommitReceipt? receipt, JournalStreamMutation mutation, long admittedAfterVersion)
    {
        if (receipt is null) return admittedAfterVersion;
        foreach (JournalStreamVersionRange range in receipt.Streams)
            if (StringComparer.Ordinal.Equals(range.StreamKey, mutation.StreamKey)) return range.AfterVersion;
        return admittedAfterVersion;
    }

    private sealed class SectionKeyComparer : IComparer<(string Stream, string Section)>
    {
        internal static readonly SectionKeyComparer Instance = new();

        public int Compare((string Stream, string Section) left, (string Stream, string Section) right)
        {
            int stream = StringComparer.Ordinal.Compare(left.Stream, right.Stream);
            return stream != 0 ? stream : StringComparer.Ordinal.Compare(left.Section, right.Section);
        }
    }
}
