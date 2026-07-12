using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

/// <summary>An (offset, length) slice into a <see cref="CaptureBuffer"/> naming one component's raw payload bytes.</summary>
internal readonly struct Segment
{
    public Segment(int offset, int length)
    {
        Offset = offset;
        Length = length;
    }

    /// <summary>Byte offset of the payload within the capture's shared buffer.</summary>
    public int Offset { get; }

    /// <summary>Payload length in bytes.</summary>
    public int Length { get; }
}

/// <summary>
/// The single growable byte buffer that backs one whole-world capture: every entity's every replicated component is
/// serialized contiguously into <see cref="Bytes"/>, and each <see cref="CapturedComponents"/> indexes it by
/// <see cref="Segment"/>. A holder (not a bare <c>byte[]</c>) so the many per-entity <see cref="CapturedComponents"/>
/// built during the capture scan can reference it before the final array exists, then all observe the one array set
/// once at the end. The buffer lives exactly as long as any capture (or projected baseline) referencing it - history
/// reachability owns its lifetime, so it is never returned to a pool while still reachable.
/// </summary>
internal sealed class CaptureBuffer
{
    /// <summary>The consolidated payload bytes for the whole capture, assigned once after the capture scan completes.</summary>
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// One entity's captured <see cref="ReplicationChannels.Replicate"/>-channel components for a single tick, stored as
/// <c>typeId -&gt; (offset, length)</c> <see cref="Segment"/>s over the capture's shared <see cref="CaptureBuffer"/>
/// instead of a <c>byte[]</c> per component. The immutable per-seq snapshot both replicators diff against a client's
/// acknowledged baseline: diffing reads payloads as <see cref="ReadOnlySpan{T}"/> over the buffer
/// (<see cref="TryGetSpan"/>) and the write path slices them straight to the outgoing wire, so no per-component array
/// is allocated on capture, diff, or write. Immutable once the capture scan finishes. A replicator instance builds and
/// reads these single-threaded on its own server-tick thread, so there is no locking.
/// </summary>
internal sealed class CapturedComponents
{
    private readonly CaptureBuffer buffer;
    private readonly Dictionary<ushort, Segment> segments;

    public CapturedComponents(CaptureBuffer buffer)
    {
        this.buffer = buffer;
        segments = new Dictionary<ushort, Segment>();
    }

    public CapturedComponents(CaptureBuffer buffer, int capacity)
    {
        this.buffer = buffer;
        segments = new Dictionary<ushort, Segment>(capacity);
    }

    /// <summary>Count of captured components.</summary>
    public int Count => segments.Count;

    /// <summary>The captured component type ids, in capture (registration) order.</summary>
    public Dictionary<ushort, Segment>.KeyCollection TypeIds => segments.Keys;

    /// <summary>The shared buffer these segments index (for a filtered owner-scope view over the same bytes).</summary>
    internal CaptureBuffer Buffer => buffer;

    /// <summary>The raw type-id to segment map (for owner-scope filtering that copies segments verbatim).</summary>
    internal Dictionary<ushort, Segment> Segments => segments;

    /// <summary>Records that component <paramref name="typeId"/> occupies <paramref name="length"/> bytes at
    /// <paramref name="offset"/> in the shared buffer.</summary>
    internal void Add(ushort typeId, int offset, int length) => segments[typeId] = new Segment(offset, length);

    /// <summary>True when this entity captured component <paramref name="typeId"/>.</summary>
    public bool Contains(ushort typeId) => segments.ContainsKey(typeId);

    /// <summary>Yields component <paramref name="typeId"/>'s payload as a span over the shared buffer, without copying.</summary>
    public bool TryGetSpan(ushort typeId, out ReadOnlySpan<byte> span)
    {
        if (segments.TryGetValue(typeId, out Segment seg))
        {
            span = new ReadOnlySpan<byte>(buffer.Bytes, seg.Offset, seg.Length);
            return true;
        }
        span = default;
        return false;
    }
}

/// <summary>
/// Reusable scratch that captures a world's <see cref="ReplicationChannels.Replicate"/>-channel components into one
/// consolidated <see cref="CaptureBuffer"/> per capture, keyed per entity by <see cref="NetId"/>. The scratch stream
/// is reused across ticks (its capacity is retained), so a steady-state capture allocates only the final exact-size
/// buffer plus the per-entity <see cref="CapturedComponents"/> - never a <c>byte[]</c> per component. Single-threaded:
/// a replicator instance owns one scratch and drives it on its own server-tick thread, so there is no locking.
/// </summary>
internal sealed class CaptureScratch
{
    private readonly MemoryStream stream = new();
    private readonly BinaryWriter writer;

    public CaptureScratch() => writer = new BinaryWriter(stream);

    /// <summary>
    /// Scans <paramref name="world"/>'s <see cref="NetId"/> entities and serializes each one's
    /// <see cref="ReplicationChannels.Replicate"/>-channel components (owner-only ones included, scoped per client
    /// later) into one shared buffer, returning <c>netId -&gt; components</c> in world <c>ForEach</c> order. Owner-only
    /// components carry <see cref="ReplicationChannels.Replicate"/>, so they are captured here and stripped per
    /// non-owning client at projection time.
    /// </summary>
    public Dictionary<long, CapturedComponents> CaptureReplicate(World world, ReplicationRegistry registry)
    {
        int replicateCount = 0;
        foreach (ComponentCodec codec in registry.Ordered)
            if ((codec.Channels & ReplicationChannels.Replicate) != 0) replicateCount++;

        stream.SetLength(0); // reset position + length, keep the buffer capacity for reuse next tick
        var buffer = new CaptureBuffer();
        var state = new Dictionary<long, CapturedComponents>();
        world.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            var comps = new CapturedComponents(buffer, replicateCount);
            foreach (ComponentCodec codec in registry.Ordered)
            {
                if ((codec.Channels & ReplicationChannels.Replicate) == 0) continue;
                int offset = (int)stream.Position;
                if (codec.CaptureInto(world, e, writer))
                    comps.Add(codec.TypeId, offset, (int)stream.Position - offset);
            }
            state[id.Value] = comps;
        });
        buffer.Bytes = stream.ToArray(); // one exact-size buffer for the whole capture, every segment now indexes it
        return state;
    }
}

/// <summary>Owner-scopes a shared capture's components for one client, over the same underlying buffer.</summary>
internal static class CaptureProjection
{
    /// <summary>
    /// Builds a filtered view of <paramref name="source"/> keeping only the components this client may see: an
    /// <see cref="ReplicationChannels.OwnerOnly"/> component is dropped unless the entity's <paramref name="netId"/>
    /// equals <paramref name="ownerNetId"/> (see <see cref="ComponentCodec.ShouldWrite"/>). The kept segments reference
    /// <paramref name="source"/>'s buffer unchanged (no payload copy), so the result is exactly the replicate-channel
    /// bytes this client is entitled to.
    /// </summary>
    public static CapturedComponents OwnerScope(CapturedComponents source, ReplicationRegistry registry,
        long netId, long? ownerNetId)
    {
        var comps = new CapturedComponents(source.Buffer, source.Count);
        foreach (KeyValuePair<ushort, Segment> kv in source.Segments)
            if (registry.TryGet(kv.Key, out ComponentCodec codec)
                && codec.ShouldWrite(ReplicationChannels.Replicate, netId, ownerNetId))
                comps.Add(kv.Key, kv.Value.Offset, kv.Value.Length);
        return comps;
    }
}
