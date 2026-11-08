using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace KhaozEngine.WorldStore.Journal;

public sealed class JournalStreamMutation
{
    private readonly IReadOnlyList<JournalEvent> events;

    public JournalStreamMutation(string streamKey, long expectedVersion, IReadOnlyList<JournalEvent> events)
    {
        StreamKey = JournalValidation.StreamKey(streamKey);
        JournalValidation.NonNegative(expectedVersion, nameof(expectedVersion));
        ExpectedVersion = expectedVersion;
        this.events = ImmutableArray.Create(JournalValidation.CopyItems(events, JournalLimits.EngineMaximumEventsPerOperation, nameof(events)));
    }

    public string StreamKey { get; }
    public long ExpectedVersion { get; }
    public IReadOnlyList<JournalEvent> Events => events;
    public int OwnedByteCount
    {
        get
        {
            int total = 0;
            foreach (JournalEvent value in events) total = checked(total + value.OwnedByteCount);
            return total;
        }
    }

    public void Validate(JournalLimits? limits = null)
    {
        limits ??= JournalLimits.Maximum;
        JournalValidation.Maximum(StreamKey.Length, limits.StreamKeyCharacters, nameof(StreamKey));
        JournalValidation.Maximum(events.Count, limits.EventsPerOperation, nameof(Events));
        foreach (JournalEvent value in events) value.Validate(limits);
    }
}
