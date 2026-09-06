using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KhaozEngine.WorldStore.Journal;

public sealed class JournalCommit
{
    private readonly ReadOnlyCollection<JournalStreamMutation> streamMutations;
    private readonly ReadOnlyCollection<JournalProjectionWrite> projectionWrites;
    private readonly byte[] resultData;
    private readonly byte[] resultChecksum;

    public JournalCommit(
        JournalOperationIdentity identity,
        IReadOnlyList<JournalStreamMutation> streamMutations,
        IReadOnlyList<JournalProjectionWrite> projectionWrites,
        string resultSchema,
        int resultSchemaVersion,
        byte[] resultData)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        JournalStreamMutation[] streams = JournalValidation.CopyItems(streamMutations, JournalLimits.EngineMaximumStreamsPerOperation, nameof(streamMutations));
        if (streams.Length == 0) throw new ArgumentException("A commit must touch at least one stream.", nameof(streamMutations));
        Array.Sort(streams, static (left, right) => StringComparer.Ordinal.Compare(left.StreamKey, right.StreamKey));
        RejectDuplicateStreams(streams);

        int eventCount = 0;
        foreach (JournalStreamMutation stream in streams) eventCount = checked(eventCount + stream.Events.Count);
        JournalValidation.Maximum(eventCount, JournalLimits.EngineMaximumEventsPerOperation, nameof(streamMutations));

        JournalProjectionWrite[] projections = JournalValidation.CopyItems(projectionWrites, JournalLimits.EngineMaximumProjectionWritesPerOperation, nameof(projectionWrites));
        Array.Sort(projections, CompareProjections);
        RejectInvalidProjections(streams, projections);

        ResultSchema = JournalValidation.Identity(resultSchema, nameof(resultSchema), JournalLimits.EngineMaximumIdentityCharacters);
        JournalValidation.Positive(resultSchemaVersion, nameof(resultSchemaVersion));
        ResultSchemaVersion = resultSchemaVersion;
        this.resultData = JournalValidation.CopyBytes(resultData, JournalLimits.EngineMaximumResultBytes, nameof(resultData));
        resultChecksum = JournalValidation.Hash(this.resultData);
        this.streamMutations = Array.AsReadOnly(streams);
        this.projectionWrites = Array.AsReadOnly(projections);
        Validate();
    }

    public JournalOperationIdentity Identity { get; }
    public IReadOnlyList<JournalStreamMutation> StreamMutations => streamMutations;
    public IReadOnlyList<JournalProjectionWrite> ProjectionWrites => projectionWrites;
    public string ResultSchema { get; }
    public int ResultSchemaVersion { get; }
    public ReadOnlyMemory<byte> ResultData => resultData;
    public ReadOnlyMemory<byte> ResultChecksum => resultChecksum;
    public int OwnedByteCount => CalculateOwnedByteCount();

    public void Validate(JournalLimits? limits = null)
    {
        limits ??= JournalLimits.Maximum;
        Identity.Validate(limits);
        JournalValidation.Maximum(streamMutations.Count, limits.StreamsPerOperation, nameof(StreamMutations));
        JournalValidation.Maximum(projectionWrites.Count, limits.ProjectionWritesPerOperation, nameof(ProjectionWrites));
        JournalValidation.Maximum(ResultSchema.Length, limits.IdentityCharacters, nameof(ResultSchema));
        JournalValidation.Maximum(resultData.Length, limits.ResultBytes, nameof(ResultData));

        int eventCount = 0;
        foreach (JournalStreamMutation stream in streamMutations)
        {
            stream.Validate(limits);
            eventCount = checked(eventCount + stream.Events.Count);
        }
        JournalValidation.Maximum(eventCount, limits.EventsPerOperation, nameof(StreamMutations));

        var projectionTotals = new Dictionary<string, (int Count, int Bytes)>(StringComparer.Ordinal);
        foreach (JournalProjectionWrite projection in projectionWrites)
        {
            projection.Validate(limits);
            projectionTotals.TryGetValue(projection.StreamKey, out (int Count, int Bytes) total);
            total = (checked(total.Count + 1), checked(total.Bytes + projection.OwnedByteCount));
            projectionTotals[projection.StreamKey] = total;
        }
        foreach (KeyValuePair<string, (int Count, int Bytes)> item in projectionTotals)
        {
            JournalValidation.Maximum(item.Value.Count, limits.ProjectionSectionsPerStream, nameof(ProjectionWrites));
            JournalValidation.Maximum(item.Value.Bytes, limits.AggregateProjectionBytesPerStream, nameof(ProjectionWrites));
        }
        JournalValidation.Maximum(CalculateOwnedByteCount(), limits.AggregateCommitBytes, nameof(OwnedByteCount));
    }

    private int CalculateOwnedByteCount()
    {
        int total = checked(Identity.OwnedByteCount + resultData.Length);
        foreach (JournalStreamMutation stream in streamMutations) total = checked(total + stream.OwnedByteCount);
        foreach (JournalProjectionWrite projection in projectionWrites) total = checked(total + projection.OwnedByteCount);
        return total;
    }

    private static void RejectDuplicateStreams(JournalStreamMutation[] streams)
    {
        for (int i = 1; i < streams.Length; i++)
            if (StringComparer.Ordinal.Equals(streams[i - 1].StreamKey, streams[i].StreamKey))
                throw new ArgumentException($"Duplicate stream key '{streams[i].StreamKey}'.", nameof(streams));
    }

    private static void RejectInvalidProjections(JournalStreamMutation[] streams, JournalProjectionWrite[] projections)
    {
        for (int i = 1; i < projections.Length; i++)
        {
            JournalProjectionWrite before = projections[i - 1];
            JournalProjectionWrite current = projections[i];
            if (StringComparer.Ordinal.Equals(before.StreamKey, current.StreamKey) && StringComparer.Ordinal.Equals(before.SectionName, current.SectionName))
                throw new ArgumentException($"Duplicate projection section '{current.StreamKey}:{current.SectionName}'.", nameof(projections));
        }

        foreach (JournalProjectionWrite projection in projections)
        {
            int index = FindStream(streams, projection.StreamKey);
            if (index < 0) throw new ArgumentException($"Projection stream '{projection.StreamKey}' is not touched by the commit.", nameof(projections));
            if (streams[index].Events.Count == 0) throw new ArgumentException($"Projection stream '{projection.StreamKey}' has no events.", nameof(projections));
        }
    }

    private static int FindStream(JournalStreamMutation[] streams, string streamKey)
    {
        int lower = 0;
        int upper = streams.Length - 1;
        while (lower <= upper)
        {
            int middle = lower + ((upper - lower) / 2);
            int comparison = StringComparer.Ordinal.Compare(streams[middle].StreamKey, streamKey);
            if (comparison == 0) return middle;
            if (comparison < 0) lower = middle + 1;
            else upper = middle - 1;
        }
        return -1;
    }

    private static int CompareProjections(JournalProjectionWrite left, JournalProjectionWrite right)
    {
        int stream = StringComparer.Ordinal.Compare(left.StreamKey, right.StreamKey);
        return stream != 0 ? stream : StringComparer.Ordinal.Compare(left.SectionName, right.SectionName);
    }
}
