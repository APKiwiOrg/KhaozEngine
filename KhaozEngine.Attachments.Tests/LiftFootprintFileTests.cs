using System;
using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class LiftFootprintFileTests
{
    const string Source = "garment.khlf";
    const int NormalCountField = 76;
    const int FirstNormal = 80;
    const int SecondNormal = 96;
    const int ThirdNormal = 112;

    static byte[] Golden() => Convert.FromHexString(
        "4b484c4601000800" + LiftFootprintTests.Digest +
        "0400000002000000" +   // Four body triangles, two parts.
        "0700000002000000" +   // Part 7 has two triangles.
        "040181010100a861" +   // Fixed keys 1 and 130. Heights 1 and 25,000 units.
        "03ff01" +             // Swinging key 255.
        "ffffffff0100000000" + // Part uint.MaxValue has one empty fixed triangle.
        "03000000" +           // Normals for body triangles 0, 2 and 3.
        "0000000000000000000000000000803f" +
        "020000000000803f0000000000000000" +
        "0300000000000000000080bf00000000");

    [Fact]
    public void Format_one_has_canonical_little_endian_bytes()
    {
        LiftFootprint input = new(LiftFootprintTests.Digest, 4,
            [new(7, [new(false, [1, 130], [0.000004, 0.25]), new(true, [255], [])]),
             new(uint.MaxValue, [default])],
            [new(0, Vector3.UnitZ), new(2, Vector3.UnitX), new(3, new Vector3(0, -1, 0))]);

        Assert.Equal(Golden(), LiftFootprintFile.Write(input));
    }

    [Fact]
    public void A_file_round_trip_keeps_body_part_triangle_and_cell_values()
    {
        byte[] bytes = Golden();
        LiftFootprint result = LiftFootprintFile.Parse(bytes, Source);

        Assert.Equal((LiftFootprintTests.Digest, 4, 8), (result.BodyRegionMapSha256, result.BodyTriangleCount, result.Level));
        Assert.Equal(new uint[] { 7, uint.MaxValue }, result.Parts.Select(part => part.PartId));
        LiftFootprintPart part = Assert.IsType<LiftFootprintPart>(result.PartById(7));
        Assert.Equal(2, part.TriangleCount);
        LiftFootprintTriangle fixedTriangle = part.Triangles[0];
        Assert.False(fixedTriangle.Swinging);
        Assert.Equal(new[] { 1, 130 }, fixedTriangle.CellKeys.ToArray());
        Assert.Equal(new ushort[] { 1, 25000 }, fixedTriangle.HeightUnits.ToArray());
        LiftFootprintTriangle swinging = part.Triangles[1];
        Assert.True(swinging.Swinging);
        Assert.Equal(new[] { 255 }, swinging.CellKeys.ToArray());
        Assert.True(swinging.HeightUnits.IsEmpty);
        LiftFootprintTriangle empty = Assert.Single(result.Parts[1].Triangles);
        Assert.False(empty.Swinging);
        Assert.True(empty.CellKeys.IsEmpty);
        Assert.True(empty.HeightUnits.IsEmpty);
        Assert.Equal(new LiftFootprintBodyNormal[] { new(0, Vector3.UnitZ), new(2, Vector3.UnitX), new(3, new Vector3(0, -1, 0)) },
            result.BodyNormals);
        Assert.Equal(Vector3.UnitX, result.NormalForBodyTriangle(2));
        Assert.Null(result.NormalForBodyTriangle(1));
        Assert.Equal(bytes, LiftFootprintFile.Write(result));
    }

    [Fact]
    public void The_largest_admitted_cell_key_round_trips_in_five_varint_bytes()
    {
        const int bodyTriangles = int.MaxValue / 64;
        const int key = bodyTriangles * 64 - 1;
        LiftFootprint footprint = new(LiftFootprintTests.Digest, bodyTriangles, [new(7, [new(true, [key], [])])]);
        byte[] bytes = LiftFootprintFile.Write(footprint);
        Assert.Equal(new byte[] { 0xbf, 0xff, 0xff, 0xff, 0x07, 0, 0, 0, 0 }, bytes[^9..]);
        LiftFootprint parsed = LiftFootprintFile.Parse(bytes, Source);
        Assert.Equal(key, parsed.Parts[0].Triangles[0].CellKeys[0]);
        Assert.Empty(parsed.BodyNormals);
        Assert.Null(parsed.NormalForBodyTriangle(bodyTriangles - 1));
    }

    [Theory]
    [InlineData("count", LiftFootprintRefusal.NormalCountExceedsRemainingBytes, NormalCountField, null, null)]
    [InlineData("descending", LiftFootprintRefusal.BodyNormalTrianglesNotAscending, SecondNormal, 1, 0L)]
    [InlineData("duplicate", LiftFootprintRefusal.BodyNormalTrianglesNotAscending, SecondNormal, 1, 0L)]
    [InlineData("outside body", LiftFootprintRefusal.BodyNormalTriangleOutsideRange, 128, 3, (long)uint.MaxValue)]
    [InlineData("nonfinite", LiftFootprintRefusal.BodyNormalNonFinite, FirstNormal + 4, 0, 0L)]
    [InlineData("not unit", LiftFootprintRefusal.BodyNormalNotUnit, FirstNormal + 4, 0, 0L)]
    [InlineData("missing", LiftFootprintRefusal.BodyNormalMissing, NormalCountField, null, 3L)]
    public void An_invalid_normal_table_names_the_refused_rule_and_field(string fault, LiftFootprintRefusal reason,
        int byteOffset, int? normalIndex, long? bodyTriangle)
    {
        byte[] golden = Golden();
        Assert.Equal(golden, LiftFootprintFile.Write(LiftFootprintFile.Parse(golden, Source)));
        byte[] broken = fault switch
        {
            "count" => Replace(golden, NormalCountField, [4, 0, 0, 0]),
            "descending" => [.. golden[..FirstNormal], .. golden[SecondNormal..ThirdNormal],
                .. golden[FirstNormal..SecondNormal], .. golden[ThirdNormal..]],
            "duplicate" => [.. Replace(golden, NormalCountField, [4, 0, 0, 0])[..SecondNormal], .. golden[FirstNormal..]],
            "outside body" => [.. Replace(golden, NormalCountField, [4, 0, 0, 0]),
                .. Replace(golden[ThirdNormal..], 0, [0xff, 0xff, 0xff, 0xff])],
            "nonfinite" => Replace(golden, FirstNormal + 4, [0, 0, 0xc0, 0x7f]),
            "not unit" => Replace(golden, FirstNormal + 12, [0, 0, 0, 0]),
            "missing" => Replace(golden[..ThirdNormal], NormalCountField, [2, 0, 0, 0]),
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };

        var error = Assert.Throws<LiftFootprintDataException>(() => LiftFootprintFile.Parse(broken, Source));

        Assert.Equal((reason, Source, byteOffset, normalIndex, bodyTriangle),
            (error.Reason, error.SourceName, error.ByteOffset, error.NormalIndex, error.BodyTriangleIndex));
        if (fault == "count")
            Assert.Equal(4u, error.NormalCount);
        if (fault == "outside body")
            Assert.Equal(4L, error.BodyTriangleCount);
        if (fault == "missing")
            Assert.Equal((7u, 0, 1, 0, 255L),
                (error.PartId, error.PartIndex, error.TriangleIndex, error.EntryIndex, error.CellKey));
    }

    [Theory]
    [InlineData("magic", LiftFootprintRefusal.MagicInvalid, 0)]
    [InlineData("version", LiftFootprintRefusal.FormatVersionUnsupported, 4)]
    [InlineData("level", LiftFootprintRefusal.LevelUnsupported, 6)]
    [InlineData("reserved", LiftFootprintRefusal.ReservedByteNonzero, 7)]
    [InlineData("body count zero", LiftFootprintRefusal.BodyTriangleCountOutsideRange, 40)]
    [InlineData("body count above limit", LiftFootprintRefusal.BodyTriangleCountOutsideRange, 40)]
    [InlineData("part count", LiftFootprintRefusal.PartCountExceedsRemainingBytes, 44)]
    [InlineData("zero part", LiftFootprintRefusal.PartIdZero, 48)]
    [InlineData("descending part", LiftFootprintRefusal.PartIdsNotAscending, 67)]
    [InlineData("duplicate part", LiftFootprintRefusal.PartIdsNotAscending, 67)]
    [InlineData("triangle count", LiftFootprintRefusal.TriangleCountExceedsRemainingBytes, 52)]
    [InlineData("entry count", LiftFootprintRefusal.EntryCountExceedsRemainingBytes, 56)]
    [InlineData("truncated magic", LiftFootprintRefusal.Truncated, 0)]
    [InlineData("truncated varint", LiftFootprintRefusal.Truncated, 62)]
    [InlineData("overflowing varint", LiftFootprintRefusal.VarintOverflow, 57)]
    [InlineData("six byte varint", LiftFootprintRefusal.VarintOverflow, 57)]
    [InlineData("noncanonical varint", LiftFootprintRefusal.VarintNonCanonical, 57)]
    [InlineData("zero delta", LiftFootprintRefusal.CellKeysNotAscending, 58)]
    [InlineData("cell outside body", LiftFootprintRefusal.CellKeyOutsideBody, 57)]
    [InlineData("cell delta beyond uint32 key", LiftFootprintRefusal.CellKeyOutsideBody, 58)]
    [InlineData("height above limit", LiftFootprintRefusal.HeightUnitsOutsideRange, 62)]
    [InlineData("trailing bytes", LiftFootprintRefusal.TrailingBytes, 128)]
    public void A_noncanonical_file_names_its_rule_and_location(string fault, LiftFootprintRefusal reason, int byteOffset)
    {
        (byte[] control, byte[] broken) = FileCase(fault);
        Assert.Equal(control, LiftFootprintFile.Write(LiftFootprintFile.Parse(control, Source)));

        var error = Assert.Throws<LiftFootprintDataException>(() => LiftFootprintFile.Parse(broken, Source));
        Assert.IsAssignableFrom<MalformedDataException>(error);
        Assert.Equal((reason, Source, byteOffset), (error.Reason, error.SourceName, error.ByteOffset));
        switch (fault)
        {
            case "version":
                Assert.Equal(2, error.FormatVersion);
                break;
            case "level":
                Assert.Equal(7, error.Level);
                break;
            case "body count zero":
            case "body count above limit":
                Assert.Equal(fault == "body count zero" ? 0L : uint.MaxValue, error.BodyTriangleCount);
                break;
            case "zero part":
            case "descending part":
            case "duplicate part":
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(broken.AsSpan(byteOffset));
                // The zero ID is the file's only part. Order faults sit at the second part.
                Assert.Equal((id, fault == "zero part" ? 0 : 1), (error.PartId, error.PartIndex));
                break;
            case "triangle count":
                Assert.Equal((7u, 0), (error.PartId, error.PartIndex));
                break;
            case "entry count":
                Assert.Equal((7u, 0, 0), (error.PartId, error.PartIndex, error.TriangleIndex));
                break;
            case "truncated varint":
                Assert.Equal((7u, 0, 0, 1), (error.PartId, error.PartIndex, error.TriangleIndex, error.EntryIndex));
                break;
            case "overflowing varint":
            case "six byte varint":
            case "noncanonical varint":
                Assert.Equal((7u, 0, 0, 0), (error.PartId, error.PartIndex, error.TriangleIndex, error.EntryIndex));
                break;
            case "zero delta":
                AssertEntry(error, 1, 1);
                break;
            case "cell outside body":
                AssertEntry(error, 0, 256);
                Assert.Equal(4L, error.BodyTriangleCount);
                break;
            case "cell delta beyond uint32 key":
                AssertEntry(error, 1, uint.MaxValue + 1L);
                Assert.Equal(4L, error.BodyTriangleCount);
                break;
            case "height above limit":
                AssertEntry(error, 1, 130);
                Assert.Equal(25001, error.HeightUnits);
                break;
        }
    }

    [Fact]
    public void Source_enrichment_keeps_every_public_identifier_and_the_original_cause()
    {
        PropertyInfo[] identifiers = typeof(LiftFootprintDataException)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(property => property.CanWrite).ToArray();
        var refusal = new LiftFootprintDataException(LiftFootprintRefusal.Truncated, "refused", new FormatException());
        for (int index = 0; index < identifiers.Length; index++)
            identifiers[index].SetValue(refusal, IdentifierSample(identifiers[index], index));

        var sourced = Assert.IsType<LiftFootprintDataException>(((IAttachmentDataException)refusal).WithSource(Source));
        Assert.Equal(refusal.Reason, sourced.Reason);
        Assert.Same(refusal, sourced.InnerException);
        Assert.Equal(Source, sourced.SourceName);
        Assert.Empty(identifiers
            .Where(property => property.Name != nameof(LiftFootprintDataException.SourceName)
                && !Equals(property.GetValue(refusal), property.GetValue(sourced)))
            .Select(property => property.Name));
    }

    static object IdentifierSample(PropertyInfo property, int index)
    {
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type == typeof(string)) return property.Name;
        if (type == typeof(int)) return 10 + index;
        if (type == typeof(uint)) return 20u + (uint)index;
        if (type == typeof(long)) return 30L + index;
        if (type == typeof(double)) return 0.5 + index;
        throw new InvalidOperationException(property.Name);
    }

    static void AssertEntry(LiftFootprintDataException error, int entryIndex, long key) =>
        Assert.Equal((7u, 0, 0, entryIndex, key),
            (error.PartId, error.PartIndex, error.TriangleIndex, error.EntryIndex, error.CellKey));

    static (byte[] Control, byte[] Broken) FileCase(string fault)
    {
        byte[] golden = Golden();
        return fault switch
        {
            "magic" => (golden, Replace(golden, 0, [0])),
            "version" => (golden, Replace(golden, 4, [2])),
            "level" => (golden, Replace(golden, 6, [7])),
            "reserved" => (golden, Replace(golden, 7, [1])),
            // Entry-free, so a zero body count breaks only the count rule.
            "body count zero" => (EntryFree(), Replace(EntryFree(), 40, [0, 0, 0, 0])),
            "body count above limit" => (golden, Replace(golden, 40, [0xff, 0xff, 0xff, 0xff])),
            "part count" => (golden, Replace(golden, 44, [0xff, 0xff, 0xff, 0xff])),
            // The only part, so a zero ID breaks positivity without breaking the ascending order.
            "zero part" => (SingleTriangle([3, 0]), Replace(SingleTriangle([3, 0]), 48, [0, 0, 0, 0])),
            "descending part" => (golden, Replace(golden, 67, [6, 0, 0, 0])),
            "duplicate part" => (golden, Replace(golden, 67, [7, 0, 0, 0])),
            "triangle count" => (golden, Replace(golden, 52, [0xff, 0xff, 0xff, 0xff])),
            "entry count" => (SingleTriangle([2, 1, 0, 0]), SingleTriangle([4, 1, 0, 0])),
            "truncated magic" => (golden, golden[..3]),
            "truncated varint" => TruncatedDelta(),
            "overflowing varint" => (SingleTriangle([3, 0]), SingleTriangle([3, 0xff, 0xff, 0xff, 0xff, 0x10])),
            "six byte varint" => (SingleTriangle([3, 0]), SingleTriangle([3, 0x80, 0x80, 0x80, 0x80, 0x80, 1])),
            "noncanonical varint" => (SingleTriangle([3, 0]), SingleTriangle([3, 0x80, 0])),
            "zero delta" => (golden, [.. golden[..58], 0, .. golden[60..]]),
            "cell outside body" => (SingleTriangle([3, 0xff, 1]), SingleTriangle([3, 0x80, 2])),
            "cell delta beyond uint32 key" =>
                (SingleTriangle([5, 1, 0xfe, 1]), SingleTriangle([5, 1, 0xff, 0xff, 0xff, 0xff, 0x0f])),
            "height above limit" => (golden, Replace(golden, 62, [0xa9])),
            "trailing bytes" => (golden, [.. golden, 0]),
            _ => throw new ArgumentException(fault),
        };
    }

    static byte[] EntryFree() => [.. Golden()[..44], 0, 0, 0, 0, 0, 0, 0, 0];

    static byte[] SingleTriangle(byte[] triangle) =>
        [.. Golden()[..44], 1, 0, 0, 0, 7, 0, 0, 0, 1, 0, 0, 0, .. triangle, 0, 0, 0, 0];

    static (byte[] Control, byte[] Broken) TruncatedDelta()
    {
        // The first delta uses five bytes, so the minimum-size checks pass even if the second delta is cut short.
        byte[] control = Replace(SingleTriangle([5, 0x80, 0x80, 0x80, 0x80, 1, 1]), 40, [1, 0, 0x40, 0]);
        return (control, Replace(control[..63], 62, [0x80]));
    }

    static byte[] Replace(byte[] input, int offset, ReadOnlySpan<byte> replacement)
    {
        byte[] result = (byte[])input.Clone();
        replacement.CopyTo(result.AsSpan(offset));
        return result;
    }
}
