using System;
using System.Buffers.Binary;

namespace KhaozEngine.Tests.Replication;

/// <summary>The legacy delta header fields, as documented on <c>ServerReplicator</c> and <c>AoiDeltaReplicator</c>.</summary>
internal readonly record struct LegacyDeltaHeader(int Baseline, int Snapshot, long[] RemovedNetIds, int ChangedCount);

/// <summary>
/// Independent reader for the legacy delta header: signed baseline, signed snapshot, removed count, Int64 removed net
/// ids and changed count, all little-endian. It never calls the production reader or writer it checks.
/// </summary>
internal static class LegacyDeltaWire
{
    public static LegacyDeltaHeader ReadHeader(ReadOnlySpan<byte> payload)
    {
        int baseline = BinaryPrimitives.ReadInt32LittleEndian(payload);
        int snapshot = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        int removedCount = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]);
        var removed = new long[removedCount];
        int offset = 12;
        for (int i = 0; i < removedCount; i++, offset += 8)
            removed[i] = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
        int changedCount = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
        return new LegacyDeltaHeader(baseline, snapshot, removed, changedCount);
    }
}
