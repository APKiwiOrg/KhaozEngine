using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld.Netcode;

public static partial class TileProtocol
{
    internal const int PreparationHeaderSize = 16;
    internal const int PreparationStateRecordSize = 50;
    internal const int PreparationTerminalRecordSize = 46;
    internal const int PreparationRecordsPerChunk = 255;
    internal const int MaxPreparationChunks = 256;
    internal const int MaxPreparationRecords = PreparationRecordsPerChunk * MaxPreparationChunks;
    const byte PreparationSchema = 1;

    internal static byte[] EncodePreparationChunk(in TilePreparationChunkHeader header,
        IReadOnlyList<TileCombatPreparation> records, int start, int count)
    {
        ValidatePreparationSlice(header, records, start, count, terminal: false);
        for (int i = 0; i < count; i++)
        {
            TileCombatPreparation record = records[start + i];
            if (!ValidPreparationRecord(record, header.ServerTick)
                || (i > 0 && record.AttackerNetId <= records[start + i - 1].AttackerNetId))
                throw new ArgumentException("Preparation records must be valid and sorted by unique attacker.", nameof(records));
        }

        byte[] bytes = CreatePreparationChunk(header, count, ServerFrameCombatPreparation, PreparationStateRecordSize);
        for (int i = 0; i < count; i++)
        {
            TileCombatPreparation record = records[start + i];
            Span<byte> row = bytes.AsSpan(PreparationHeaderSize + i * PreparationStateRecordSize, PreparationStateRecordSize);
            WritePreparationIdentity(row, record.AttackerNetId, record.TargetNetId, record.AttackId, record.Revision, record.PresentationKey);
            BinaryPrimitives.WriteInt64LittleEndian(row.Slice(32, 8), record.PrepareTick);
            BinaryPrimitives.WriteInt64LittleEndian(row.Slice(40, 8), record.ImpactTick);
            row[48] = record.StrikeTicks;
            row[49] = record.CadenceTicks;
        }
        return bytes;
    }

    internal static bool TryDecodePreparationChunk(ReadOnlySpan<byte> data,
        out TilePreparationChunkHeader header, List<TileCombatPreparation> into)
    {
        header = default;
        if (into is null) return false;
        into.Clear();
        if (!TryReadPreparationHeader(data, ServerFrameCombatPreparation, PreparationStateRecordSize,
            terminal: false, out TilePreparationChunkHeader candidate, out int count)) return false;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> row = data.Slice(PreparationHeaderSize + i * PreparationStateRecordSize, PreparationStateRecordSize);
            var record = new TileCombatPreparation(BinaryPrimitives.ReadInt64LittleEndian(row.Slice(0, 8)),
                BinaryPrimitives.ReadInt64LittleEndian(row.Slice(8, 8)), BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(16, 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(24, 4)), BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(28, 4)),
                BinaryPrimitives.ReadInt64LittleEndian(row.Slice(32, 8)), BinaryPrimitives.ReadInt64LittleEndian(row.Slice(40, 8)),
                row[48], row[49]);
            if (!ValidPreparationRecord(record, candidate.ServerTick)
                || (into.Count > 0 && record.AttackerNetId <= into[^1].AttackerNetId))
            {
                into.Clear();
                return false;
            }
            into.Add(record);
        }
        header = candidate;
        return true;
    }

    internal static byte[] EncodePreparationTerminalChunk(in TilePreparationChunkHeader header,
        IReadOnlyList<TileCombatTerminal> records, int start, int count)
    {
        ValidatePreparationSlice(header, records, start, count, terminal: true);
        var order = new TileCombatTerminalOrder(count);
        for (int i = 0; i < count; i++)
        {
            TileCombatTerminal record = records[start + i];
            if (!ValidPreparationTerminal(record, header.ServerTick)
                || !order.TryAdd(record))
                throw new ArgumentException("Terminal records must be valid and preserve category and attack transition order.", nameof(records));
        }

        byte[] bytes = CreatePreparationChunk(header, count, ServerFrameCombatPreparationTerminal, PreparationTerminalRecordSize);
        for (int i = 0; i < count; i++)
        {
            TileCombatTerminal record = records[start + i];
            Span<byte> row = bytes.AsSpan(PreparationHeaderSize + i * PreparationTerminalRecordSize, PreparationTerminalRecordSize);
            WritePreparationIdentity(row, record.AttackerNetId, record.TargetNetId, record.AttackId, record.Revision, record.PresentationKey);
            BinaryPrimitives.WriteInt64LittleEndian(row.Slice(32, 8), record.ImpactTick);
            row[40] = (byte)record.Kind;
            row[41] = (byte)record.Reason;
            BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(42, 2), record.Amount);
            row[44] = record.HitKind;
            row[45] = record.Flags;
        }
        return bytes;
    }

    internal static bool TryDecodePreparationTerminalChunk(ReadOnlySpan<byte> data,
        out TilePreparationChunkHeader header, List<TileCombatTerminal> into)
    {
        header = default;
        if (into is null) return false;
        into.Clear();
        if (!TryReadPreparationHeader(data, ServerFrameCombatPreparationTerminal, PreparationTerminalRecordSize,
            terminal: true, out TilePreparationChunkHeader candidate, out int count)) return false;
        var order = new TileCombatTerminalOrder(count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> row = data.Slice(PreparationHeaderSize + i * PreparationTerminalRecordSize, PreparationTerminalRecordSize);
            var record = new TileCombatTerminal(BinaryPrimitives.ReadInt64LittleEndian(row.Slice(0, 8)),
                BinaryPrimitives.ReadInt64LittleEndian(row.Slice(8, 8)), BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(16, 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(24, 4)), BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(28, 4)),
                BinaryPrimitives.ReadInt64LittleEndian(row.Slice(32, 8)), (TileCombatTerminalKind)row[40],
                (TileCombatPreparationEndReason)row[41], BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(42, 2)), row[44], row[45]);
            if (!ValidPreparationTerminal(record, candidate.ServerTick)
                || !order.TryAdd(record))
            {
                into.Clear();
                return false;
            }
            into.Add(record);
        }
        header = candidate;
        return true;
    }

    internal static bool ValidPreparationHeader(in TilePreparationChunkHeader header, int count, bool terminal) =>
        header.ServerTick >= 0 && header.ChunkCount is >= 1 and <= MaxPreparationChunks
        && header.ChunkIndex < header.ChunkCount && count is >= 0 and <= PreparationRecordsPerChunk
        && (count > 0 || (!terminal && header.ChunkCount == 1));

    internal static bool ValidPreparationRecord(in TileCombatPreparation record, long serverTick)
    {
        if (record.AttackerNetId == 0 || record.TargetNetId == 0 || record.AttackId == 0 || record.Revision == 0
            || record.PrepareTick < 0 || record.ImpactTick <= record.PrepareTick
            // A deferred schedule stays active while it is overdue by fewer than its strike ticks.
            || !TileCombatPreparationDeferral.IsLive(record, serverTick)) return false;
        // Both ticks are nonnegative and ordered before subtraction, so an extreme wire value cannot wrap this span.
        long lead = record.ImpactTick - record.PrepareTick;
        return record.StrikeTicks > 0 && record.StrikeTicks <= lead && lead <= record.CadenceTicks;
    }

    internal static bool ValidPreparationTerminal(in TileCombatTerminal record, long serverTick)
    {
        if (record.AttackerNetId == 0 || record.TargetNetId == 0 || record.AttackId == 0 || record.Revision == 0
            || record.ImpactTick < 0) return false;
        return record.Kind switch
        {
            TileCombatTerminalKind.Resolved => record.ImpactTick == serverTick
                && record.Reason == TileCombatPreparationEndReason.None && (record.Flags & ~3) == 0,
            TileCombatTerminalKind.Cancelled => record.Reason is >= TileCombatPreparationEndReason.Disengaged
                and <= TileCombatPreparationEndReason.RulesUnavailable && record.Amount == 0 && record.HitKind == 0 && record.Flags == 0,
            _ => false
        };
    }

    static void ValidatePreparationSlice<T>(in TilePreparationChunkHeader header,
        IReadOnlyList<T> records, int start, int count, bool terminal)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (start < 0 || start > records.Count) throw new ArgumentOutOfRangeException(nameof(start));
        if (count < 0 || count > PreparationRecordsPerChunk || count > records.Count - start)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (!ValidPreparationHeader(header, count, terminal))
            throw new ArgumentException("Invalid preparation chunk header or empty set.", nameof(header));
    }

    static bool TryReadPreparationHeader(ReadOnlySpan<byte> data, byte tag, int recordSize, bool terminal,
        out TilePreparationChunkHeader header, out int count)
    {
        header = default;
        count = 0;
        if (data.Length < PreparationHeaderSize || data[0] != tag || data[1] != PreparationSchema) return false;
        var candidate = new TilePreparationChunkHeader(BinaryPrimitives.ReadInt64LittleEndian(data.Slice(2, 8)),
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(10, 2)), BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(12, 2)));
        int records = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(14, 2));
        if (!ValidPreparationHeader(candidate, records, terminal)
            || data.Length != PreparationHeaderSize + records * recordSize) return false;
        header = candidate;
        count = records;
        return true;
    }

    static byte[] CreatePreparationChunk(in TilePreparationChunkHeader header, int count, byte tag, int recordSize)
    {
        var bytes = new byte[PreparationHeaderSize + count * recordSize];
        bytes[0] = tag;
        bytes[1] = PreparationSchema;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(2, 8), header.ServerTick);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10, 2), header.ChunkIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12, 2), header.ChunkCount);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14, 2), (ushort)count);
        return bytes;
    }

    static void WritePreparationIdentity(Span<byte> row, long attacker, long target, ulong id, uint revision, uint key)
    {
        BinaryPrimitives.WriteInt64LittleEndian(row.Slice(0, 8), attacker);
        BinaryPrimitives.WriteInt64LittleEndian(row.Slice(8, 8), target);
        BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(16, 8), id);
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(24, 4), revision);
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(28, 4), key);
    }
}
