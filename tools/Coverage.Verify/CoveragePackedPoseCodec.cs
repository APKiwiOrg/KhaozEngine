using System;
using System.Buffers.Binary;
using System.Numerics;

namespace KhaozEngine.Tools.Coverage;

internal static class CoveragePackedPoseCodec
{
    const int HeaderLength = 32;
    const uint Magic = 0x5050454b;
    const uint Endian = 0x01020304;
    static readonly CoverageInputLocation WriterLocation = new("$", "KEPP");

    internal static CoveragePackedHeader ReadHeader(ReadOnlySpan<byte> header, long actualLength, CoverageInputLocation location)
    {
        if (header.Length != HeaderLength)
            throw Error(CoverageBuildInputRefusal.PackedHeaderInvalid, location, 0, expectedLength: HeaderLength, actualLength: header.Length);
        if (UInt(header, 0) != Magic) throw Error(CoverageBuildInputRefusal.PackedHeaderInvalid, location, 0);
        uint version = UInt(header, 4);
        if (version != 1) throw Error(CoverageBuildInputRefusal.UnsupportedVersion, location, 4, version: version);
        if (UInt(header, 8) != HeaderLength) throw Error(CoverageBuildInputRefusal.PackedHeaderInvalid, location, 8);
        if (UInt(header, 12) != Endian) throw Error(CoverageBuildInputRefusal.PackedHeaderInvalid, location, 12);
        return Layout(UInt(header, 16), UInt(header, 20), UInt(header, 24), UInt(header, 28), actualLength, location);
    }

    static CoveragePackedHeader Layout(uint bones, uint commands, uint records, uint stride, long actualLength, CoverageInputLocation location)
    {
        BoundedCount(bones, 1024, 16, location);
        BoundedCount(commands, 4, 20, location);
        if (records == 0) throw Error(CoverageBuildInputRefusal.PackedCountMismatch, location, 24, count: records);
        long expectedStride = checked(64L + 64L * bones + 144L * commands);
        if (stride != expectedStride) throw Error(CoverageBuildInputRefusal.PackedHeaderInvalid, location, 28);
        long expectedLength = checked(HeaderLength + records * expectedStride);
        if (expectedLength > CoverageBuildInput.MaxPackedChunkBytes)
            throw Error(CoverageBuildInputRefusal.CapacityExceeded, location, 24, count: expectedLength, limit: CoverageBuildInput.MaxPackedChunkBytes);
        if (actualLength != expectedLength)
            throw Error(CoverageBuildInputRefusal.PackedChunkLengthMismatch, location, 24, expectedLength: expectedLength, actualLength: actualLength);
        return new((int)bones, (int)commands, (int)records, (int)stride);
    }

    static void BoundedCount(uint count, int limit, int offset, CoverageInputLocation location)
    {
        if (count == 0) throw Error(CoverageBuildInputRefusal.PackedHeaderInvalid, location, offset, count: count, limit: limit);
        if (count > limit) throw Error(CoverageBuildInputRefusal.CapacityExceeded, location, offset, count: count, limit: limit);
    }

    static void ValidateLayout(CoveragePackedHeader header, CoverageInputLocation location) =>
        Layout(unchecked((uint)header.PaletteCount), unchecked((uint)header.CommandCount), unchecked((uint)header.RecordCount),
            unchecked((uint)header.RecordStride), checked(HeaderLength + (long)header.RecordCount * header.RecordStride), location);

    internal static void ValidateRecord(ReadOnlySpan<byte> record, CoveragePackedHeader header, CoverageInputLocation location)
    {
        ValidateLayout(header, location);
        long start = checked(HeaderLength + (long)(location.RecordIndex ?? 0) * header.RecordStride);
        if (record.Length != header.RecordStride)
            throw Error(CoverageBuildInputRefusal.PackedChunkLengthMismatch, location, start, expectedLength: header.RecordStride, actualLength: record.Length);
        int commandsStart = 64 + 64 * header.PaletteCount;
        for (int offset = 0; offset < commandsStart; offset += 4) ValidateWord(record, offset, start, location);
        Span<uint> ids = stackalloc uint[4];
        for (int i = 0; i < header.CommandCount; i++)
        {
            int offset = commandsStart + 144 * i;
            uint id = UInt(record, offset);
            for (int prior = 0; prior < i; prior++)
                if (ids[prior] == id) throw Error(CoverageBuildInputRefusal.PackedCountMismatch, location, start + offset);
            ids[i] = id;
            if (BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(offset + 4, 8)) == 0)
                throw Error(CoverageBuildInputRefusal.InvalidValue, location, start + offset + 4);
            for (int word = offset + 12; word < offset + 144; word += 4) ValidateWord(record, word, start, location);
        }
    }

    static void ValidateWord(ReadOnlySpan<byte> record, int offset, long start, CoverageInputLocation location)
    {
        // Inspect exponent bits before any floating-point operation, including signaling NaNs.
        if ((UInt(record, offset) & 0x7f800000u) == 0x7f800000u)
            throw Error(CoverageBuildInputRefusal.PackedWordNonFinite, location, start + offset);
    }

    internal static CoverageNumericPose ReadRecord(ReadOnlySpan<byte> record, CoveragePackedHeader header, CoverageInputLocation location)
    {
        ValidateRecord(record, header, location);
        int offset = 0;
        Matrix4x4 actor = ReadMatrix(record, ref offset);
        var palette = new Matrix4x4[header.PaletteCount];
        for (int i = 0; i < palette.Length; i++) palette[i] = ReadMatrix(record, ref offset);
        var commands = new CoveragePackedCommand[header.CommandCount];
        for (int i = 0; i < commands.Length; i++)
        {
            uint id = UInt(record, offset);
            ulong instance = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(offset + 4, 8));
            offset += 12;
            Matrix4x4 equipment = ReadMatrix(record, ref offset);
            Matrix4x4 coverage = ReadMatrix(record, ref offset);
            commands[i] = new(id, instance, equipment, coverage, Single(record, offset));
            offset += 4;
        }
        return new(actor, palette, commands);
    }

    internal static void WriteHeader(Span<byte> destination, CoveragePackedHeader header)
    {
        ValidateLayout(header, WriterLocation);
        if (destination.Length != HeaderLength) throw new ArgumentException("Destination must be exactly one header.", nameof(destination));
        ReadOnlySpan<uint> words = [Magic, 1, HeaderLength, Endian, (uint)header.PaletteCount,
            (uint)header.CommandCount, (uint)header.RecordCount, (uint)header.RecordStride];
        for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(i * 4, 4), words[i]);
    }

    internal static void WriteRecord(Span<byte> destination, CoveragePackedHeader header, CoverageNumericPose pose)
    {
        ValidateLayout(header, WriterLocation);
        ArgumentNullException.ThrowIfNull(pose);
        ArgumentNullException.ThrowIfNull(pose.Palette);
        ArgumentNullException.ThrowIfNull(pose.Commands);
        if (destination.Length != header.RecordStride) throw new ArgumentException("Destination must be exactly one record.", nameof(destination));
        if (pose.Palette.Length != header.PaletteCount || pose.Commands.Length != header.CommandCount)
            throw new ArgumentException("Pose must match the header shape.", nameof(pose));
        int offset = 0;
        WriteMatrix(destination, ref offset, pose.ActorModel);
        foreach (var matrix in pose.Palette) WriteMatrix(destination, ref offset, matrix);
        foreach (var command in pose.Commands)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(offset, 4), command.Id);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(offset + 4, 8), command.EquipmentInstanceId);
            offset += 12;
            WriteMatrix(destination, ref offset, command.EquipmentWorld);
            WriteMatrix(destination, ref offset, command.CoverageWorld);
            WriteSingle(destination, offset, command.TransitionWeight);
            offset += 4;
        }
        ValidateRecord(destination, header, WriterLocation);
    }

    static Matrix4x4 ReadMatrix(ReadOnlySpan<byte> bytes, ref int offset)
    {
        int p = offset;
        offset += 64;
        return new(Single(bytes, p), Single(bytes, p + 4), Single(bytes, p + 8), Single(bytes, p + 12),
            Single(bytes, p + 16), Single(bytes, p + 20), Single(bytes, p + 24), Single(bytes, p + 28),
            Single(bytes, p + 32), Single(bytes, p + 36), Single(bytes, p + 40), Single(bytes, p + 44),
            Single(bytes, p + 48), Single(bytes, p + 52), Single(bytes, p + 56), Single(bytes, p + 60));
    }

    static void WriteMatrix(Span<byte> bytes, ref int offset, Matrix4x4 matrix)
    {
        ReadOnlySpan<float> values = [matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24, matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44];
        foreach (float value in values) { WriteSingle(bytes, offset, value); offset += 4; }
    }

    static uint UInt(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
    static float Single(ReadOnlySpan<byte> bytes, int offset) => BitConverter.UInt32BitsToSingle(UInt(bytes, offset));
    static void WriteSingle(Span<byte> bytes, int offset, float value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(offset, 4), BitConverter.SingleToUInt32Bits(value));

    static CoverageBuildDataException Error(CoverageBuildInputRefusal reason, CoverageInputLocation location, long offset,
        long? count = null, long? limit = null, long? expectedLength = null, long? actualLength = null, long? version = null) =>
        new(reason, "Packed pose bytes do not satisfy the transport contract.")
        {
            Path = location.Path, Identity = location.Identity, ChunkIndex = location.ChunkIndex, RecordIndex = location.RecordIndex,
            ByteOffset = offset, Count = count, Limit = limit, ExpectedLength = expectedLength, ActualLength = actualLength, FormatVersion = version,
        };
}
