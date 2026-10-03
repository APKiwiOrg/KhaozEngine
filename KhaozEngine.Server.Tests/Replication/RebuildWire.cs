using System;
using System.Buffers.Binary;

namespace KhaozEngine.Tests.Replication;

/// <summary>The fixed format 2 header fields plus the removed and changed entity counts.</summary>
internal readonly record struct RebuildWireHeader(byte Format, byte Flags, ulong Epoch, uint Snapshot, uint Baseline,
    int RemovedCount, int ChangedCount);

/// <summary>
/// Independent reader for the format 2 body header: <c>[format][flags][epoch u64][snapshot u32][baseline u32]</c>, then
/// <c>[removedCount i32][removedNetId i64...][changedCount i32]</c>, all little-endian. It never calls a production
/// reader.
/// </summary>
internal static class RebuildWire
{
    /// <summary>Bytes in the fixed format 2 header before the removed count.</summary>
    public const int HeaderBytes = 18;

    public static RebuildWireHeader ReadHeader(ReadOnlySpan<byte> body)
    {
        byte format = body[0];
        byte flags = body[1];
        ulong epoch = BinaryPrimitives.ReadUInt64LittleEndian(body[2..]);
        uint snapshot = BinaryPrimitives.ReadUInt32LittleEndian(body[10..]);
        uint baseline = BinaryPrimitives.ReadUInt32LittleEndian(body[14..]);
        int removed = BinaryPrimitives.ReadInt32LittleEndian(body[HeaderBytes..]);
        int changedAt = HeaderBytes + 4 + (removed * 8);
        int changed = BinaryPrimitives.ReadInt32LittleEndian(body[changedAt..]);
        return new RebuildWireHeader(format, flags, epoch, snapshot, baseline, removed, changed);
    }

    /// <summary>The removed entity ids, read after the fixed header.</summary>
    public static long[] ReadRemovedNetIds(ReadOnlySpan<byte> body)
    {
        int removed = BinaryPrimitives.ReadInt32LittleEndian(body[HeaderBytes..]);
        var ids = new long[removed];
        for (int i = 0; i < removed; i++)
            ids[i] = BinaryPrimitives.ReadInt64LittleEndian(body[(HeaderBytes + 4 + (i * 8))..]);
        return ids;
    }
}
