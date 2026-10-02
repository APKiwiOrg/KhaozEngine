using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace KhaozEngine.Replication;

/// <summary>
/// The bounded per-viewer table of retained immutable projections, shared in shape by the server writer slot and the
/// client receiver. Pins (at most three ids, such as an acknowledged baseline, a repair candidate or the latest
/// publication) are charged inside the same count and byte limits as every other entry. Unpinned entries are pruned
/// oldest first by insertion ordinal, never by numeric sequence, because sequences wrap. Retained bytes are the full
/// <c>Length</c> of each distinct backing array reachable from any entry, counted once by reference identity.
/// Single-threaded, like the replicators and the client view.
/// </summary>
internal sealed class ProjectionRetention
{
    /// <summary>The most ids one retention may pin.</summary>
    internal const int MaxProspectivePins = 3;

    private readonly DeltaRebuildOptions options;
    private readonly List<Entry> entries = new();   // insertion order, oldest first
    private readonly Dictionary<byte[], int> backingRefs = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ReplicationPacketId> pins = new();
    private readonly HashSet<byte[]> scratchBackings = new(ReferenceEqualityComparer.Instance);
    private long payloadBytes;

    // List position is the insertion ordinal: entries only append and remove, never reorder.
    private readonly record struct Entry(ReplicationPacketId Id, ReplicationProjection Projection);

    public ProjectionRetention(DeltaRebuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
    }

    /// <summary>Retained projections, pinned ones included.</summary>
    internal int Count => entries.Count;

    /// <summary>Distinct reachable backing bytes, at most <see cref="DeltaRebuildOptions.MaxRetainedPayloadBytes"/>.</summary>
    internal int PayloadBytes => (int)payloadBytes;

    /// <summary>Entities summed over retained projections, an entity shared by two projections counted twice.</summary>
    internal int Entities { get; private set; }

    /// <summary>Component frames summed over retained projections, zero-byte and opaque frames included.</summary>
    internal int Components { get; private set; }

    /// <summary>The ids the last successful <see cref="TryRetain"/> pinned.</summary>
    internal IReadOnlySet<ReplicationPacketId> Pins => pins;

    public bool TryGet(ReplicationPacketId id, out ReplicationProjection projection)
    {
        int index = IndexOf(id);
        projection = index < 0 ? null! : entries[index].Projection;
        return index >= 0;
    }

    /// <summary>
    /// Retains <paramref name="projection"/> as <paramref name="id"/>, replaces the pin set with
    /// <paramref name="prospectivePins"/>, and prunes the oldest unpinned entries until the count and byte limits
    /// hold, as one atomic step. On failure nothing changes. <see cref="DeltaRebuildFailure.CapacityExceeded"/> means
    /// the projection can never fit alone. <see cref="DeltaRebuildFailure.RetentionPressure"/> means it fits alone but
    /// not beside the pinned projections. The new entry is never pruned by its own insertion.
    /// </summary>
    /// <exception cref="ArgumentException">The id is already retained, more than three pins were named, or a pin
    /// names an id that is neither retained nor <paramref name="id"/>.</exception>
    public bool TryRetain(ReplicationPacketId id, ReplicationProjection projection,
        IReadOnlySet<ReplicationPacketId> prospectivePins, out DeltaRebuildFailure failure)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(prospectivePins);
        if (prospectivePins.Count > MaxProspectivePins)
            throw new ArgumentException($"At most {MaxProspectivePins} ids may be pinned.", nameof(prospectivePins));
        if (IndexOf(id) >= 0) throw new ArgumentException($"Projection {id} is already retained.", nameof(id));
        foreach (ReplicationPacketId pin in prospectivePins)
            if (pin != id && IndexOf(pin) < 0)
                throw new ArgumentException($"Pinned projection {pin} is not retained.", nameof(prospectivePins));

        if (projection.EntityCount > options.MaxEntities || projection.ComponentCount > options.MaxComponents
            || projection.BackingBytes > options.MaxRetainedPayloadBytes)
        {
            failure = DeltaRebuildFailure.CapacityExceeded;
            return false;
        }

        // Everything that must stay after this call: the new projection and every pinned one.
        int keptCount = 1;
        scratchBackings.Clear();
        long keptBytes = Charge(projection);
        foreach (ReplicationPacketId pin in prospectivePins)
        {
            if (pin == id) continue;
            keptCount++;
            keptBytes += Charge(entries[IndexOf(pin)].Projection);
        }
        scratchBackings.Clear();
        if (keptCount > options.MaxRetainedProjections || keptBytes > options.MaxRetainedPayloadBytes)
        {
            failure = DeltaRebuildFailure.RetentionPressure;
            return false;
        }

        // Commit. The kept set fits, so pruning every other entry would fit, and pruning stops as soon as it does.
        entries.Add(new Entry(id, projection));
        AddRefs(projection);
        pins.Clear();
        pins.UnionWith(prospectivePins);
        for (int i = 0; i < entries.Count && OverLimit();)
        {
            Entry entry = entries[i];
            if (entry.Id == id || pins.Contains(entry.Id)) { i++; continue; }
            entries.RemoveAt(i);
            ReleaseRefs(entry.Projection);
        }
        Debug.Assert(!OverLimit(), "A kept set within limits must leave retention within limits.");
        failure = DeltaRebuildFailure.None;
        return true;
    }

    /// <summary>Drops every entry and pin.</summary>
    public void Clear()
    {
        entries.Clear();
        backingRefs.Clear();
        pins.Clear();
        payloadBytes = 0;
        Entities = 0;
        Components = 0;
    }

    /// <summary>The distinct backing arrays every retained projection reaches.</summary>
    internal IEnumerable<byte[]> BackingArraysForTest() => backingRefs.Keys;

    private bool OverLimit() =>
        entries.Count > options.MaxRetainedProjections || payloadBytes > options.MaxRetainedPayloadBytes;

    private int IndexOf(ReplicationPacketId id)
    {
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].Id == id) return i;
        return -1;
    }

    // Bytes of the projection's backings not yet counted in scratchBackings.
    private long Charge(ReplicationProjection projection)
    {
        long bytes = 0;
        foreach (byte[] backing in projection.Backings)
            if (scratchBackings.Add(backing)) bytes += backing.Length;
        return bytes;
    }

    private void AddRefs(ReplicationProjection projection)
    {
        foreach (byte[] backing in projection.Backings)
        {
            backingRefs.TryGetValue(backing, out int refs);
            if (refs == 0) payloadBytes += backing.Length;
            backingRefs[backing] = refs + 1;
        }
        Entities += projection.EntityCount;
        Components += projection.ComponentCount;
    }

    private void ReleaseRefs(ReplicationProjection projection)
    {
        foreach (byte[] backing in projection.Backings)
        {
            int refs = backingRefs[backing] - 1;
            if (refs > 0)
            {
                backingRefs[backing] = refs;
                continue;
            }
            backingRefs.Remove(backing);
            payloadBytes -= backing.Length;
        }
        Entities -= projection.EntityCount;
        Components -= projection.ComponentCount;
    }
}
