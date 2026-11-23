using System;
using System.Buffers.Binary;

namespace KhaozEngine.Attachments;

// The reader owns the cursor and the structural context of the field currently being read.
internal ref struct LiftFootprintFileReader(ReadOnlySpan<byte> bytes)
{
    const int PartHeaderBytes = 2 * sizeof(uint);
    const int DigestBytes = 32;

    readonly ReadOnlySpan<byte> input = bytes;
    int offset;
    int bodyTriangleCount;
    uint? partId;
    int? partIndex;
    int? triangleIndex;
    int? entryIndex;

    internal LiftFootprint Read()
    {
        if (!Take(LiftFootprintFile.Magic.Length).SequenceEqual(LiftFootprintFile.Magic))
            throw Error(LiftFootprintRefusal.MagicInvalid, "The file must start with KHLF.", 0);

        int field = offset;
        int version = ReadUInt16();
        if (version != LiftFootprintFile.FormatVersion)
            throw Error(LiftFootprintRefusal.FormatVersionUnsupported, "The footprint format version is unsupported.", field, version: version);

        field = offset;
        int level = Take(1)[0];
        if (level != BodyCell.Level)
            throw Error(LiftFootprintRefusal.LevelUnsupported, "The footprint grid level is unsupported.", field, level: level);

        field = offset;
        if (Take(1)[0] != 0)
            throw Error(LiftFootprintRefusal.ReservedByteNonzero, "The reserved byte must be zero.", field);

        string digest = Convert.ToHexStringLower(Take(DigestBytes));
        field = offset;
        uint declaredBodyTriangles = ReadUInt32();
        LiftFootprint.CheckBodyTriangleCount(declaredBodyTriangles, field);
        bodyTriangleCount = (int)declaredBodyTriangles;

        field = offset;
        uint partCount = ReadUInt32();
        EnsureCountFits(partCount, PartHeaderBytes, 0, field, LiftFootprintRefusal.PartCountExceedsRemainingBytes);
        var parts = new LiftFootprintPart[(int)partCount];
        uint previousId = 0;
        for (int index = 0; index < parts.Length; index++)
        {
            partIndex = index;
            int laterPartHeaders = (parts.Length - index - 1) * PartHeaderBytes;
            parts[index] = ReadPart(previousId, index, laterPartHeaders);
            previousId = parts[index].PartId;
        }

        partId = null;
        partIndex = null;
        if (Remaining != 0)
            throw Error(LiftFootprintRefusal.TrailingBytes, "The file has bytes after its last part.", offset);

        return new LiftFootprint(digest, bodyTriangleCount, parts);
    }

    LiftFootprintPart ReadPart(uint previousId, int index, int laterPartHeaders)
    {
        partId = null;
        int field = offset;
        uint id = ReadUInt32();
        LiftFootprint.CheckPartId(id, previousId, index, field);
        partId = id;

        field = offset;
        uint triangleCount = ReadUInt32();
        EnsureCountFits(triangleCount, 1, laterPartHeaders, field, LiftFootprintRefusal.TriangleCountExceedsRemainingBytes);

        var builder = new LiftFootprintPart.Builder();
        for (int triangle = 0; triangle < (int)triangleCount; triangle++)
        {
            triangleIndex = triangle;
            int laterHeaders = laterPartHeaders + (int)triangleCount - 1 - triangle;
            ReadTriangle(builder, laterHeaders);
        }

        triangleIndex = null;
        return builder.Build(id);
    }

    void ReadTriangle(LiftFootprintPart.Builder builder, int laterHeaders)
    {
        int field = offset;
        uint header = ReadVarint();
        bool swinging = (header & 1) != 0;
        int count = (int)(header >> 1);
        int minimumEntryBytes = swinging ? 1 : 1 + sizeof(ushort);
        EnsureCountFits((uint)count, minimumEntryBytes, laterHeaders, field, LiftFootprintRefusal.EntryCountExceedsRemainingBytes);

        int previous = 0;
        int limit = bodyTriangleCount * BodyCell.CellsPerTriangle;
        for (int entry = 0; entry < count; entry++)
        {
            entryIndex = entry;
            field = offset;
            uint delta = ReadVarint();
            if (entry > 0 && delta == 0)
                throw Error(LiftFootprintRefusal.CellKeysNotAscending, "Cell keys must increase strictly.", field, cellKey: previous);

            long key = previous + (long)delta;
            if (key >= limit)
                throw Error(LiftFootprintRefusal.CellKeyOutsideBody, "The cell key lies outside the declared body triangles.",
                    field, cellKey: key, declaredBodyTriangles: bodyTriangleCount);

            previous = (int)key;
            builder.AddCell(previous);
        }

        if (!swinging)
        {
            for (int entry = 0; entry < count; entry++)
            {
                entryIndex = entry;
                field = offset;
                ushort height = ReadUInt16();
                if (height > LiftFootprintTriangle.MaxHeightUnits)
                    throw Error(LiftFootprintRefusal.HeightUnitsOutsideRange, "A stored height must be at most 25,000 units.",
                        field, cellKey: builder.CellKey(entry), heightUnits: height);
                builder.AddHeight(height);
            }
        }

        entryIndex = null;
        builder.EndTriangle(swinging);
    }

    int Remaining => input.Length - offset;

    void EnsureCountFits(uint count, int minimumBytes, int laterHeaders, int field, LiftFootprintRefusal reason)
    {
        if (count * (long)minimumBytes + laterHeaders > Remaining)
            throw Error(reason, "The declared count cannot fit in the remaining bytes.", field);
    }

    uint ReadVarint()
    {
        int start = offset;
        uint value = 0;
        for (int group = 0; group < 5; group++)
        {
            if (Remaining == 0)
                throw Error(LiftFootprintRefusal.Truncated, "The variable-length integer is incomplete.", start);

            byte current = input[offset++];
            if (group == 4 && current > 0x0f)
                throw Error(LiftFootprintRefusal.VarintOverflow, "The variable-length integer exceeds 32 bits or five bytes.", start);

            uint payload = (uint)(current & 0x7f);
            value |= payload << (7 * group);
            if ((current & 0x80) == 0)
            {
                if (group > 0 && payload == 0)
                    throw Error(LiftFootprintRefusal.VarintNonCanonical, "The variable-length integer has a redundant zero group.", start);
                return value;
            }
        }

        // The fifth group cannot continue, because the UInt32 range check above accepts four payload bits only.
        throw Error(LiftFootprintRefusal.VarintOverflow, "The variable-length integer exceeds five bytes.", start);
    }

    uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(sizeof(uint)));
    ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(sizeof(ushort)));

    ReadOnlySpan<byte> Take(int count)
    {
        if (count > Remaining)
            throw Error(LiftFootprintRefusal.Truncated, "The footprint field is incomplete.", offset);

        ReadOnlySpan<byte> value = input.Slice(offset, count);
        offset += count;
        return value;
    }

    LiftFootprintDataException Error(LiftFootprintRefusal reason, string message, int field,
        int? version = null, int? level = null, long? cellKey = null, int? declaredBodyTriangles = null, int? heightUnits = null) =>
        new(reason, message)
        {
            ByteOffset = field,
            FormatVersion = version,
            Level = level,
            PartId = partId,
            PartIndex = partIndex,
            TriangleIndex = triangleIndex,
            EntryIndex = entryIndex,
            CellKey = cellKey,
            BodyTriangleCount = declaredBodyTriangles,
            HeightUnits = heightUnits,
        };
}
