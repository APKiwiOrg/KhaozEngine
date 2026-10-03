using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace KhaozEngine.Replication;

/// <summary>
/// Validates one format 2 body against its exact baseline and reconstructs the complete projection privately. The
/// layout is the one <see cref="RebuildDeltaEncoding"/> writes. Every count is checked against the remaining bytes and
/// the limits before anything is allocated for it, every id is unique, and the body must be consumed exactly.
/// </summary>
/// <remarks>
/// Read discipline, so each known component of the result is read exactly once per packet. A received frame is decoded
/// once into staging as it is parsed: a framed extension within its stated length, an unframed built-in over the rest
/// of the body, which is how its boundary is found. Once the final map is known, only the known components carried
/// over from the baseline are decoded, from their exact slices. Unknown extensions stay opaque and are never read.
/// The result is copied into one fresh exact-size backing, so a retained projection never reaches the caller's
/// packet buffer and is charged no more than its own complete keyframe. A failure throws before anything is retained
/// or published. Single-threaded, one instance per receiver.
/// </remarks>
internal sealed class RebuildDeltaReader
{
    // netId, isNew flag, removed component count, entity terminator.
    private const int MinEntryBytes = sizeof(long) + sizeof(byte) + sizeof(int) + sizeof(ushort);

    private readonly DeltaRebuildOptions options;

    // Per-packet scratch, bounded by the validated counts and cleared on every read.
    private readonly HashSet<long> removedIds = new();
    private readonly Dictionary<long, int> changedIndex = new();
    private readonly List<Entry> changed = new();
    private readonly List<ushort> removedTypes = new();
    private readonly List<Frame> received = new();
    private readonly HashSet<ushort> entityTypes = new();
    private readonly Dictionary<ushort, int> replacements = new();
    private readonly List<Frame> planned = new();
    private readonly List<(long NetId, int Start, int Count)> plannedEntities = new();

    private readonly record struct Entry(long NetId, bool Full, int RemovedStart, int RemovedCount, int FrameStart,
        int FrameCount);

    // A payload slice of the received body or of a baseline backing. Carried frames come from the baseline.
    private readonly record struct Frame(ushort TypeId, byte[] Source, int Offset, int Length, bool Carried);

    public RebuildDeltaReader(DeltaRebuildOptions options) => this.options = options;

    /// <summary>The fixed header of one body: the packet id and, for a delta, the baseline id in the same epoch.</summary>
    internal readonly record struct Header(ReplicationPacketId Id, ReplicationPacketId? Baseline)
    {
        public bool IsKeyframe => Baseline is null;
    }

    /// <summary>
    /// Reads and validates the 18-byte fixed header: format 2, flags 0 or 1, a nonzero epoch, and a zero baseline field
    /// for a keyframe. Reads nothing past the header.
    /// </summary>
    internal static bool TryReadHeader(ReadOnlySpan<byte> body, out Header header, out string? error)
    {
        header = default;
        if (body.Length < RebuildDeltaEncoding.HeaderBytes)
            return Fail($"Format 2 body has {body.Length} bytes, below the {RebuildDeltaEncoding.HeaderBytes}-byte header.",
                out error);
        if (body[0] != RebuildDeltaEncoding.Format)
            return Fail($"Unknown replication body format {body[0]}.", out error);
        byte flags = body[1];
        if ((flags & ~RebuildDeltaEncoding.KeyframeFlag) != 0)
            return Fail($"Format 2 flags 0x{flags:X2} carry an unknown bit.", out error);
        ulong epoch = BinaryPrimitives.ReadUInt64LittleEndian(body[2..]);
        if (epoch == 0) return Fail("Format 2 epoch zero never names a stream.", out error);
        uint snapshot = BinaryPrimitives.ReadUInt32LittleEndian(body[10..]);
        uint baseline = BinaryPrimitives.ReadUInt32LittleEndian(body[14..]);
        bool keyframe = flags == RebuildDeltaEncoding.KeyframeFlag;
        if (keyframe && baseline != 0)
            return Fail($"Format 2 keyframe names baseline {baseline}, which must be zero.", out error);
        header = new Header(new ReplicationPacketId(epoch, snapshot),
            keyframe ? null : new ReplicationPacketId(epoch, baseline));
        error = null;
        return true;
    }

    /// <summary>
    /// Validates the body after its header and reconstructs the projection it names. <paramref name="staging"/> must
    /// already be reset. On return it holds a decoded value for every known component of the result, and the caller
    /// finishes it for that projection.
    /// </summary>
    /// <exception cref="DeltaRebuildException"><see cref="DeltaRebuildFailure.MalformedPacket"/> for any count, id,
    /// flag, length, terminator, consumption or codec fault. <see cref="DeltaRebuildFailure.CapacityExceeded"/> when
    /// the result would exceed the entity, frame or complete keyframe limit.</exception>
    public ReplicationProjection Read(byte[] source, int offset, int length, ReplicationProjection basis, bool keyframe,
        ProjectionStaging staging)
    {
        Clear();
        try
        {
            Parse(source, offset, offset + length, basis, keyframe, staging);
            return Reconstruct(basis, staging);
        }
        finally
        {
            Clear();   // never keep a packet buffer or a baseline backing alive from scratch
        }
    }

    private void Parse(byte[] source, int pos, int end, ReplicationProjection basis, bool keyframe,
        ProjectionStaging staging)
    {
        pos += RebuildDeltaEncoding.HeaderBytes;
        int removedCount = ReadInt32(source, ref pos, end, "removed entity count");
        if (removedCount < 0) throw Malformed($"Removed entity count {removedCount} is negative.");
        if (keyframe && removedCount != 0) throw Malformed($"Keyframe removes {removedCount} entities.");
        if ((long)removedCount * sizeof(long) > end - pos)
            throw Malformed($"Removed entity count {removedCount} exceeds the {end - pos} remaining bytes.");
        if (removedCount > basis.EntityCount)
            throw Malformed($"Removed entity count {removedCount} exceeds the baseline's {basis.EntityCount} entities.");
        for (int i = 0; i < removedCount; i++)
        {
            long netId = ReadInt64(source, ref pos, end, "removed net id");
            if (!basis.TryGetEntity(netId, out _)) throw Malformed($"Removed net id {netId} is not in the baseline.");
            if (!removedIds.Add(netId)) throw Malformed($"Removed net id {netId} repeats.");
        }

        int changedCount = ReadInt32(source, ref pos, end, "changed entity count");
        if (changedCount < 0) throw Malformed($"Changed entity count {changedCount} is negative.");
        if ((long)changedCount * MinEntryBytes > end - pos)
            throw Malformed($"Changed entity count {changedCount} exceeds the {end - pos} remaining bytes.");
        if (changedCount > options.MaxEntities)
            throw Capacity($"Body changes {changedCount} entities, limit {options.MaxEntities}.");
        int receivedFrames = 0;
        for (int i = 0; i < changedCount; i++)
        {
            long netId = ReadInt64(source, ref pos, end, "changed net id");
            if (removedIds.Contains(netId)) throw Malformed($"Net id {netId} is both removed and changed.");
            if (changedIndex.ContainsKey(netId)) throw Malformed($"Changed net id {netId} repeats.");
            byte flag = ReadByte(source, ref pos, end, "entry flag");
            if (flag > 1) throw Malformed($"Net id {netId} has entry flag {flag}.");
            bool full = flag == 1;
            ProjectedEntity? previous = null;
            if (keyframe && !full) throw Malformed($"Keyframe carries a partial entry for net id {netId}.");
            if (!full && !basis.TryGetEntity(netId, out previous))
                throw Malformed($"Partial entry for net id {netId}, which is not in the baseline.");

            int removedComps = ReadInt32(source, ref pos, end, "removed component count");
            if (removedComps < 0) throw Malformed($"Net id {netId} removed component count {removedComps} is negative.");
            if (full && removedComps != 0)
                throw Malformed($"Full entry for net id {netId} removes {removedComps} components.");
            if ((long)removedComps * sizeof(ushort) > end - pos)
                throw Malformed($"Net id {netId} removed component count {removedComps} exceeds the remaining bytes.");
            entityTypes.Clear();
            int removedStart = removedTypes.Count;
            for (int r = 0; r < removedComps; r++)
            {
                ushort typeId = ReadUInt16(source, ref pos, end, "removed type id");
                if (typeId == 0 || !previous!.Contains(typeId))
                    throw Malformed($"Net id {netId} removes type id {typeId}, which its baseline lacks.");
                if (!entityTypes.Add(typeId)) throw Malformed($"Net id {netId} removes type id {typeId} twice.");
                removedTypes.Add(typeId);
            }

            int frameStart = received.Count;
            while (true)
            {
                if (end - pos < sizeof(ushort)) throw Malformed($"Net id {netId} has no entity terminator.");
                ushort typeId = ReadUInt16(source, ref pos, end, "type id");
                if (typeId == 0) break;
                if (!entityTypes.Add(typeId))
                    throw Malformed(removedTypes.IndexOf(typeId, removedStart) >= 0
                        ? $"Net id {netId} both removes and replaces type id {typeId}."
                        : $"Net id {netId} carries type id {typeId} twice.");
                if (++receivedFrames > options.MaxComponents)
                    throw Capacity($"Body carries over {options.MaxComponents} component frames.");
                int frameLength;
                if (ReplicationRegistry.IsExtension(typeId))
                {
                    frameLength = Read7BitLength(source, ref pos, end, netId, typeId);
                    if (frameLength > end - pos)
                        throw Malformed($"Net id {netId} type id {typeId} states {frameLength} bytes, {end - pos} remain.");
                    staging.Decode(netId, new ProjectedComponent(typeId, source, pos, frameLength));
                }
                else
                {
                    frameLength = staging.DecodeUnframed(netId, typeId, source, pos, end - pos);
                }
                received.Add(new Frame(typeId, source, pos, frameLength, Carried: false));
                pos += frameLength;
            }
            changedIndex.Add(netId, changed.Count);
            changed.Add(new Entry(netId, full, removedStart, removedComps, frameStart, received.Count - frameStart));
        }
        if (pos != end) throw Malformed($"Format 2 body has {end - pos} trailing bytes.");
    }

    private ReplicationProjection Reconstruct(ReplicationProjection basis, ProjectionStaging staging)
    {
        foreach (KeyValuePair<long, ProjectedEntity> kv in basis.Entities)
        {
            if (removedIds.Contains(kv.Key)) continue;
            if (changedIndex.TryGetValue(kv.Key, out int index)) PlanChanged(changed[index], kv.Value);
            else PlanCarried(kv.Key, kv.Value);
        }
        foreach (Entry entry in changed)
            if (!basis.TryGetEntity(entry.NetId, out _)) PlanChanged(entry, previous: null);

        if (plannedEntities.Count > options.MaxEntities)
            throw Capacity($"Projection would have {plannedEntities.Count} entities, limit {options.MaxEntities}.");
        if (planned.Count > options.MaxComponents)
            throw Capacity($"Projection would have {planned.Count} component frames, limit {options.MaxComponents}.");
        long payloadBytes = 0;
        long keyframeBytes = RebuildDeltaEncoding.HeaderBytes + sizeof(int) + sizeof(int)
            + ((long)plannedEntities.Count * MinEntryBytes);
        foreach (Frame frame in planned)
        {
            payloadBytes += frame.Length;
            keyframeBytes += sizeof(ushort) + frame.Length;
            if (ReplicationRegistry.IsExtension(frame.TypeId)) keyframeBytes += SevenBitBytes(frame.Length);
        }
        if (keyframeBytes + options.EnvelopeBytes > options.MaxKeyframeBytes)
            throw Capacity($"Projection's complete keyframe would be {keyframeBytes + options.EnvelopeBytes} bytes, " +
                $"limit {options.MaxKeyframeBytes}.");

        // Every keyframe byte limit is at most a quarter of the retained budget, so the payload fits a backing.
        byte[] backing = payloadBytes == 0 ? Array.Empty<byte>() : new byte[payloadBytes];
        var entities = new List<KeyValuePair<long, ProjectedEntity>>(plannedEntities.Count);
        int at = 0;
        foreach ((long netId, int start, int count) in plannedEntities)
        {
            var frames = new ProjectedComponent[count];
            for (int i = 0; i < count; i++)
            {
                Frame frame = planned[start + i];
                Buffer.BlockCopy(frame.Source, frame.Offset, backing, at, frame.Length);
                frames[i] = new ProjectedComponent(frame.TypeId, backing, at, frame.Length);
                at += frame.Length;
                if (frame.Carried) staging.Decode(netId, frames[i]);
            }
            entities.Add(new KeyValuePair<long, ProjectedEntity>(netId, new ProjectedEntity(frames)));
        }
        return ReplicationProjection.Create(entities, options);
    }

    // A full entry is exactly its received frames. A partial entry keeps its baseline frames in order, minus removals,
    // with replacements in place, then appends newly added types in wire order.
    private void PlanChanged(Entry entry, ProjectedEntity? previous)
    {
        int start = planned.Count;
        if (entry.Full || previous is null)
        {
            for (int i = 0; i < entry.FrameCount; i++) planned.Add(received[entry.FrameStart + i]);
        }
        else
        {
            replacements.Clear();
            for (int i = 0; i < entry.FrameCount; i++) replacements[received[entry.FrameStart + i].TypeId] = i;
            entityTypes.Clear();
            for (int r = 0; r < entry.RemovedCount; r++) entityTypes.Add(removedTypes[entry.RemovedStart + r]);
            for (int i = 0; i < previous.Count; i++)
            {
                ProjectedComponent c = previous[i];
                if (entityTypes.Contains(c.TypeId)) continue;
                if (replacements.Remove(c.TypeId, out int r)) planned.Add(received[entry.FrameStart + r]);
                else planned.Add(new Frame(c.TypeId, c.Backing, c.Offset, c.Length, Carried: true));
            }
            for (int i = 0; i < entry.FrameCount; i++)
            {
                Frame frame = received[entry.FrameStart + i];
                if (replacements.ContainsKey(frame.TypeId)) planned.Add(frame);
            }
        }
        plannedEntities.Add((entry.NetId, start, planned.Count - start));
    }

    private void PlanCarried(long netId, ProjectedEntity entity)
    {
        int start = planned.Count;
        for (int i = 0; i < entity.Count; i++)
        {
            ProjectedComponent c = entity[i];
            planned.Add(new Frame(c.TypeId, c.Backing, c.Offset, c.Length, Carried: true));
        }
        plannedEntities.Add((netId, start, entity.Count));
    }

    private void Clear()
    {
        removedIds.Clear();
        changedIndex.Clear();
        changed.Clear();
        removedTypes.Clear();
        received.Clear();
        entityTypes.Clear();
        replacements.Clear();
        planned.Clear();
        plannedEntities.Clear();
    }

    private static int ReadInt32(byte[] source, ref int pos, int end, string field)
    {
        if (end - pos < sizeof(int)) throw Truncated(field);
        int value = BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(pos));
        pos += sizeof(int);
        return value;
    }

    private static long ReadInt64(byte[] source, ref int pos, int end, string field)
    {
        if (end - pos < sizeof(long)) throw Truncated(field);
        long value = BinaryPrimitives.ReadInt64LittleEndian(source.AsSpan(pos));
        pos += sizeof(long);
        return value;
    }

    private static ushort ReadUInt16(byte[] source, ref int pos, int end, string field)
    {
        if (end - pos < sizeof(ushort)) throw Truncated(field);
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(pos));
        pos += sizeof(ushort);
        return value;
    }

    private static byte ReadByte(byte[] source, ref int pos, int end, string field)
    {
        if (end - pos < 1) throw Truncated(field);
        return source[pos++];
    }

    // The 7-bit length BinaryWriter.Write7BitEncodedInt writes: at most five bytes, the fifth carrying four bits, and
    // never negative.
    private static int Read7BitLength(byte[] source, ref int pos, int end, long netId, ushort typeId)
    {
        uint result = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            if (pos >= end) throw Malformed($"Net id {netId} type id {typeId} length is truncated.");
            byte b = source[pos++];
            if (shift == 28 && b > 0x0F) throw Malformed($"Net id {netId} type id {typeId} length is overlong.");
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                if (result > int.MaxValue) throw Malformed($"Net id {netId} type id {typeId} length is negative.");
                return (int)result;
            }
        }
        throw Malformed($"Net id {netId} type id {typeId} length is overlong.");
    }

    private static int SevenBitBytes(int value)
    {
        int bytes = 1;
        for (uint v = (uint)value; v >= 0x80; v >>= 7) bytes++;
        return bytes;
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    private static DeltaRebuildException Truncated(string field) =>
        Malformed($"Format 2 body ends inside the {field}.");

    private static DeltaRebuildException Malformed(string message) =>
        new(DeltaRebuildFailure.MalformedPacket, message);

    private static DeltaRebuildException Capacity(string message) =>
        new(DeltaRebuildFailure.CapacityExceeded, message);
}
