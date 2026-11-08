using System;
using System.Collections.Generic;

namespace KhaozEngine.WorldStore.Journal;

public sealed partial class InMemoryMutationJournalStore
{
    /// <summary>Flips the checksum of one committed event to exercise its stored-data refusal.
    /// The replacement owns its bytes and no mutable store collection escapes.</summary>
    internal void CorruptStoredEventChecksumForTests(string streamKey, long streamVersion)
    {
        lock (gate)
        {
            StreamState stream = state.Streams[streamKey];
            var events = new List<JournalStoredEvent>(stream.Events);
            int index = events.FindIndex(value => value.StreamVersion == streamVersion);
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(streamVersion), streamVersion, "The stored event does not exist.");
            JournalStoredEvent stored = events[index];
            byte[] checksum = stored.PayloadChecksum.ToArray();
            checksum[0] ^= 0xff;
            events[index] = new JournalStoredEvent(stored.StreamKey, stored.StreamVersion, stored.OperationId,
                stored.OperationOrdinal, stored.EventType, stored.EventSchemaVersion, stored.Payload.ToArray(),
                checksum, stored.CommittedAtUtc);
            var streams = new Dictionary<string, StreamState>(state.Streams, StringComparer.Ordinal)
            {
                [streamKey] = stream with { Events = events },
            };
            state = state with { Streams = streams };
        }
    }

    /// <summary>Flips one stored projection checksum without exposing writable storage or changing its data.</summary>
    internal void CorruptStoredProjectionChecksumForTests(string streamKey, string sectionName)
    {
        lock (gate)
        {
            StreamState stream = state.Streams[streamKey];
            JournalProjectionSection stored = stream.Projections[sectionName];
            byte[] checksum = stored.DataChecksum.ToArray();
            checksum[0] ^= 0xff;
            var sections = new Dictionary<string, JournalProjectionSection>(stream.Projections, StringComparer.Ordinal)
            {
                [sectionName] = new JournalProjectionSection(stored.StreamKey, stored.SectionName, stored.SourceVersion,
                    stored.ProjectionSchema, stored.ProjectionSchemaVersion, stored.Data.ToArray(), checksum, stored.UpdatedAtUtc),
            };
            var streams = new Dictionary<string, StreamState>(state.Streams, StringComparer.Ordinal)
            {
                [streamKey] = stream with { Projections = sections },
            };
            state = state with { Streams = streams };
        }
    }
}
