using System;
using System.Buffers.Binary;

namespace KhaozEngine.Render3D;

/// <summary>Strict, bounded KERT version 1 little-endian asset codec.</summary>
public static class CoverageRecessionTableBinary
{
    const int HeaderLength = 212;

    /// <summary>Read one complete asset. Declared byte length and all records are checked before allocating table arrays.</summary>
    public static CoverageRecessionTable Read(ReadOnlySpan<byte> bytes, string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        if (bytes.Length < 4) throw Truncated(bytes, identity);
        if (!bytes[..4].SequenceEqual("KERT"u8))
            throw new CoverageTableDataException(CoverageTableRefusal.InvalidMagic, "A recession table must begin with KERT.")
            { Identity = identity, ByteOffset = 0 };
        if (bytes.Length < 8) throw Truncated(bytes, identity);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (version != 1)
            throw new CoverageTableDataException(CoverageTableRefusal.UnsupportedVersion, "Only recession table version 1 is supported.")
            { Identity = identity, ByteOffset = 4, FormatVersion = version };
        if (bytes.Length < HeaderLength) throw Truncated(bytes, identity);
        uint groups = BinaryPrimitives.ReadUInt32LittleEndian(bytes[200..]);
        uint entries = BinaryPrimitives.ReadUInt32LittleEndian(bytes[204..]);
        uint states = BinaryPrimitives.ReadUInt32LittleEndian(bytes[208..]);
        int expected = GetByteLength(groups, entries, states, identity);
        int groupCount = (int)groups, entryCount = (int)entries, stateCount = (int)states;
        int scalarOffset = HeaderLength + groupCount * 4;
        int scalarCount = groupCount * entryCount;
        int stateOffset = scalarOffset + scalarCount * 4;
        if (bytes.Length < expected) throw Truncated(bytes, identity, groupCount, scalarOffset, stateOffset);
        if (bytes.Length > expected)
            throw new CoverageTableDataException(CoverageTableRefusal.TrailingData, "Bytes follow the declared recession table payload.")
            { Identity = identity, ByteOffset = expected };

        for (int group = 0, offset = HeaderLength; group < groupCount; group++, offset += 4)
            CoverageRecessionTable.ValidateRepresentative(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]), group, identity, offset);
        for (int i = 0, offset = scalarOffset; i < scalarCount; i++, offset += 4)
            CoverageRecessionTable.ValidateScalar(BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]), i % groupCount, i / groupCount, identity, offset);
        CoverageStateKey? previous = null;
        for (int index = 0, offset = stateOffset; index < stateCount; index++, offset += 12)
        {
            CoverageStateEntry state = ReadState(bytes, offset);
            CoverageRecessionTable.ValidateState(state, previous, entryCount, index, identity, offset);
            previous = state.Key;
        }

        var tableIdentity = new CoverageRecessionIdentity(
            Digest(bytes, 8), Digest(bytes, 40), Digest(bytes, 72), Digest(bytes, 104), Digest(bytes, 136), Digest(bytes, 168));
        int[] representatives = new int[groupCount];
        float[] recessions = new float[scalarCount];
        CoverageStateEntry[] ownedStates = new CoverageStateEntry[stateCount];
        for (int group = 0, offset = HeaderLength; group < groupCount; group++, offset += 4)
            representatives[group] = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
        for (int i = 0, offset = scalarOffset; i < scalarCount; i++, offset += 4)
            recessions[i] = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]));
        for (int index = 0, offset = stateOffset; index < stateCount; index++, offset += 12)
            ownedStates[index] = ReadState(bytes, offset);
        return new(tableIdentity, representatives, recessions, ownedStates);
    }

    /// <summary>Write the canonical wire representation without changing zero signs or subnormal words.</summary>
    public static byte[] Write(CoverageRecessionTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        byte[] bytes = new byte[GetByteLength(table.PositionGroupCount, table.EntryCount, table.States.Length)];
        "KERT"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        WriteDigest(bytes, 8, table.Identity.HostSha256);
        WriteDigest(bytes, 40, table.Identity.HostMapSha256);
        WriteDigest(bytes, 72, table.Identity.ConfigurationSha256);
        WriteDigest(bytes, 104, table.Identity.RefinementSha256);
        WriteDigest(bytes, 136, table.Identity.ArithmeticSha256);
        WriteDigest(bytes, 168, table.Identity.StateSchemaSha256);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(200), (uint)table.PositionGroupCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(204), (uint)table.EntryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(208), (uint)table.States.Length);
        int offset = HeaderLength;
        foreach (int representative in table.Representatives)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), representative);
            offset += 4;
        }
        for (int entry = 0; entry < table.EntryCount; entry++)
            foreach (float recession in table.Entry(entry))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), BitConverter.SingleToUInt32Bits(recession));
                offset += 4;
            }
        foreach (CoverageStateEntry state in table.States)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), state.Key.Value);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 8), state.EntryIndex);
            offset += 12;
        }
        return bytes;
    }

    internal static int GetByteLength(long groups, long entries, long states, string? identity = null)
    {
        if (groups == 0)
            throw new CoverageTableDataException(CoverageTableRefusal.ZeroGroups, "A table requires position groups.")
            { Identity = identity, ByteOffset = identity is null ? null : 200 };
        CheckCount(groups, "PositionGroupCount", 200, identity);
        CheckCount(entries, "EntryCount", 204, identity);
        CheckCount(states, "StateCount", 208, identity);
        long length;
        try { length = checked(HeaderLength + groups * 4 + groups * entries * 4 + states * 12); }
        catch (OverflowException) { throw LengthOverflow(identity); }
        if (length > Array.MaxLength) throw LengthOverflow(identity, length);
        return (int)length;
    }

    static void CheckCount(long count, string member, int offset, string? identity)
    {
        if (count > Array.MaxLength)
            throw new CoverageTableDataException(CoverageTableRefusal.LengthOverflow, "A table count exceeds the managed asset capacity.")
            { Identity = identity, ByteOffset = identity is null ? null : offset, MemberName = member, Count = count, Limit = Array.MaxLength };
    }

    static CoverageTableDataException LengthOverflow(string? identity, long? count = null) =>
        new(CoverageTableRefusal.LengthOverflow, "The checked table byte length exceeds the managed asset capacity.")
        { Identity = identity, ByteOffset = identity is null ? null : 200, Count = count, Limit = Array.MaxLength };

    static CoverageTableDataException Truncated(ReadOnlySpan<byte> bytes, string identity,
        int groups = 0, int scalarOffset = 0, int stateOffset = 0)
    {
        int? group = null, entry = null, state = null;
        CoverageStateKey? key = null;
        if (groups != 0 && bytes.Length >= HeaderLength)
        {
            if (bytes.Length < scalarOffset) group = (bytes.Length - HeaderLength) / 4;
            else if (bytes.Length < stateOffset)
            {
                int scalar = (bytes.Length - scalarOffset) / 4;
                group = scalar % groups;
                entry = scalar / groups;
            }
            else
            {
                state = (bytes.Length - stateOffset) / 12;
                int offset = stateOffset + state.Value * 12;
                if (bytes.Length - offset >= 8) key = new(BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]));
            }
        }
        return new(CoverageTableRefusal.Truncated, "The recession table ends before its declared payload is complete.")
        { Identity = identity, ByteOffset = bytes.Length, GroupIndex = group, EntryIndex = entry, StateIndex = state, Key = key };
    }

    static CoverageStateEntry ReadState(ReadOnlySpan<byte> bytes, int offset) =>
        new(new(BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..])), BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 8)..]));

    static string Digest(ReadOnlySpan<byte> bytes, int offset) => Convert.ToHexStringLower(bytes.Slice(offset, 32));

    static void WriteDigest(Span<byte> bytes, int offset, string digest)
    {
        for (int i = 0; i < 32; i++)
            bytes[offset + i] = (byte)((Nibble(digest[i * 2]) << 4) | Nibble(digest[i * 2 + 1]));
    }

    static int Nibble(char digit) => digit <= '9' ? digit - '0' : digit - 'a' + 10;
}
