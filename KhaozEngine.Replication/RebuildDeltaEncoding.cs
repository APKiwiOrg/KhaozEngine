using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace KhaozEngine.Replication;

/// <summary>
/// The format 2 body: an 18-byte header <c>[format 2][flags][epoch u64][snapshotSeq u32][baselineSeq u32]</c>, then the
/// unchanged legacy sections <c>[removedCount][removedNetId...][changedCount]</c> and per changed entity
/// <c>[netId][isNew][removedCompCount][removedTypeId...][(typeId,[len],data)...][0]</c>, all little-endian. Flag bit 0
/// marks a keyframe, which starts from empty state with baseline field zero, no removed entities and only full entries.
/// Projections already hold capture (registration) order and raw captured bytes, so the encoder writes them as they
/// are. The 7-bit length frames only extension ids, as the legacy writer does.
/// </summary>
internal static class RebuildDeltaEncoding
{
    internal const byte Format = 2;
    internal const byte KeyframeFlag = 1;
    internal const int HeaderBytes = 18;

    /// <summary>
    /// Writes the body for <paramref name="id"/> into <paramref name="bw"/>. A null <paramref name="baseline"/> writes a
    /// keyframe from empty state. Otherwise the body diffs <paramref name="current"/> from the projection retained as
    /// <paramref name="baseline"/>.
    /// </summary>
    public static void Write(BinaryWriter bw, ReplicationPacketId id,
        (ReplicationPacketId Id, ReplicationProjection Projection)? baseline, ReplicationProjection current,
        List<long> scratch)
    {
        Span<byte> header = stackalloc byte[HeaderBytes];
        header[0] = Format;
        header[1] = baseline is null ? KeyframeFlag : (byte)0;
        BinaryPrimitives.WriteUInt64LittleEndian(header[2..], id.Epoch);
        BinaryPrimitives.WriteUInt32LittleEndian(header[10..], id.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(header[14..], baseline?.Id.Sequence ?? 0u);
        bw.Write(header);

        ReplicationProjection basis = baseline?.Projection ?? ReplicationProjection.Empty;

        scratch.Clear();
        foreach (long netId in basis.Entities.Keys)
            if (!current.Entities.ContainsKey(netId)) scratch.Add(netId);
        bw.Write(scratch.Count);
        foreach (long netId in scratch) bw.Write(netId);

        scratch.Clear();
        foreach (KeyValuePair<long, ProjectedEntity> kv in current.Entities)
            if (!basis.TryGetEntity(kv.Key, out ProjectedEntity previous) || Changed(previous, kv.Value))
                scratch.Add(kv.Key);
        bw.Write(scratch.Count);
        foreach (long netId in scratch)
        {
            ProjectedEntity entity = current.Entities[netId];
            basis.TryGetEntity(netId, out ProjectedEntity? previous);
            WriteEntity(bw, netId, previous, entity);
        }
    }

    /// <summary>Keyframe body bytes before any entity: the header, the removed count and the changed count.</summary>
    internal const int KeyframePrefixBytes = HeaderBytes + sizeof(int) + sizeof(int);

    /// <summary>Bytes of one full entry before its frames: net id, entry flag, removed component count and the
    /// entity terminator. Also the smallest possible changed entry.</summary>
    internal const int EntryBytes = sizeof(long) + sizeof(byte) + sizeof(int) + sizeof(ushort);

    /// <summary>Bytes one frame takes on the wire: type id, the 7-bit length for an extension id, then the payload.
    /// The writer's capacity check and the receiver's reconstruction check both sum this, so they cannot drift.</summary>
    internal static int FrameBytes(ushort typeId, int payloadLength) =>
        sizeof(ushort) + payloadLength + (ReplicationRegistry.IsExtension(typeId) ? SevenBitLength(payloadLength) : 0);

    /// <summary>The exact byte length of a keyframe body for <paramref name="projection"/>, envelope excluded.</summary>
    public static long KeyframeBytes(ReplicationProjection projection)
    {
        long bytes = KeyframePrefixBytes;
        foreach (ProjectedEntity entity in projection.Entities.Values)
        {
            bytes += EntryBytes;
            for (int i = 0; i < entity.Count; i++) bytes += FrameBytes(entity[i].TypeId, entity[i].Length);
        }
        return bytes;
    }

    private static bool Changed(ProjectedEntity previous, ProjectedEntity current)
    {
        if (previous.Count != current.Count) return true;
        for (int i = 0; i < current.Count; i++)
        {
            ProjectedComponent c = current[i];
            if (!previous.TryGet(c.TypeId, out ProjectedComponent p) || !c.Span.SequenceEqual(p.Span)) return true;
        }
        return false;
    }

    // A full entry when previous is null, otherwise removed type ids in baseline order, then added or changed frames
    // in the current (registration) order.
    private static void WriteEntity(BinaryWriter bw, long netId, ProjectedEntity? previous, ProjectedEntity current)
    {
        bw.Write(netId);
        bw.Write(previous is null ? (byte)1 : (byte)0);

        int removed = 0;
        if (previous is not null)
            for (int i = 0; i < previous.Count; i++)
                if (!current.Contains(previous[i].TypeId)) removed++;
        bw.Write(removed);
        if (previous is not null)
            for (int i = 0; i < previous.Count; i++)
                if (!current.Contains(previous[i].TypeId)) bw.Write(previous[i].TypeId);

        for (int i = 0; i < current.Count; i++)
        {
            ProjectedComponent c = current[i];
            if (previous is not null && previous.TryGet(c.TypeId, out ProjectedComponent p) && c.Span.SequenceEqual(p.Span))
                continue;
            bw.Write(c.TypeId);
            if (ReplicationRegistry.IsExtension(c.TypeId)) bw.Write7BitEncodedInt(c.Length);
            bw.Write(c.Span);
        }
        bw.Write((ushort)0);
    }

    private static int SevenBitLength(int value)
    {
        int bytes = 1;
        for (uint v = (uint)value; v >= 0x80; v >>= 7) bytes++;
        return bytes;
    }
}
