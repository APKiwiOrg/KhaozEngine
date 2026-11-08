using System;
using System.Buffers.Binary;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public class CoverageRecessionTableBinaryTests
{
    [Fact]
    public void Format_one_reads_and_writes_independently_authored_little_endian_bytes()
    {
        byte[] bytes = Fixture();
        CoverageRecessionTable table = CoverageRecessionTableBinary.Read(bytes, "wire");
        Assert.Equal(new CoverageRecessionIdentity(
            "0101010101010101010101010101010101010101010101010101010101010101",
            "0202020202020202020202020202020202020202020202020202020202020202",
            "0303030303030303030303030303030303030303030303030303030303030303",
            "0404040404040404040404040404040404040404040404040404040404040404",
            "0505050505050505050505050505050505050505050505050505050505050505",
            "0606060606060606060606060606060606060606060606060606060606060606"), table.Identity);
        Assert.Equal(new[] { 0, -1 }, table.Representatives.ToArray());
        Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(table.Entry(0)[1]));
        Assert.Equal(1u, BitConverter.SingleToUInt32Bits(table.Entry(1)[0]));
        Assert.Equal(0x7f7fffffu, BitConverter.SingleToUInt32Bits(table.Entry(1)[1]));
        Assert.True(table.TrySelect(new(0), out int first));
        Assert.Equal(0, first);
        Assert.True(table.TrySelect(new(ulong.MaxValue), out int last));
        Assert.Equal(1, last);
        Assert.Equal(bytes, CoverageRecessionTableBinary.Write(table));
    }

    [Theory]
    [InlineData("magic", CoverageTableRefusal.InvalidMagic, 0, null, null, null)]
    [InlineData("version", CoverageTableRefusal.UnsupportedVersion, 4, null, null, null)]
    [InlineData("groups", CoverageTableRefusal.ZeroGroups, 200, null, null, null)]
    [InlineData("group-count", CoverageTableRefusal.LengthOverflow, 200, null, null, null)]
    [InlineData("entry-count", CoverageTableRefusal.LengthOverflow, 204, null, null, null)]
    [InlineData("state-count", CoverageTableRefusal.LengthOverflow, 208, null, null, null)]
    [InlineData("product", CoverageTableRefusal.LengthOverflow, 200, null, null, null)]
    [InlineData("payload", CoverageTableRefusal.LengthOverflow, 200, null, null, null)]
    [InlineData("representative", CoverageTableRefusal.InvalidRepresentative, 216, 1, null, null)]
    [InlineData("infinity", CoverageTableRefusal.NonFiniteRecession, 224, 1, 0, null)]
    [InlineData("nan", CoverageTableRefusal.NonFiniteRecession, 224, 1, 0, null)]
    [InlineData("negative-infinity", CoverageTableRefusal.NonFiniteRecession, 224, 1, 0, null)]
    [InlineData("negative", CoverageTableRefusal.NegativeRecession, 224, 1, 0, null)]
    [InlineData("negative-subnormal", CoverageTableRefusal.NegativeRecession, 224, 1, 0, null)]
    [InlineData("duplicate", CoverageTableRefusal.DuplicateKey, 248, null, 1, 0UL)]
    [InlineData("unsorted", CoverageTableRefusal.UnsortedKeys, 248, null, 1, 0UL)]
    [InlineData("entry-low", CoverageTableRefusal.InvalidEntryIndex, 256, null, -2, ulong.MaxValue)]
    [InlineData("entry-high", CoverageTableRefusal.InvalidEntryIndex, 256, null, 2, ulong.MaxValue)]
    [InlineData("trailing", CoverageTableRefusal.TrailingData, 260, null, null, null)]
    public void Malformed_binary_refuses_before_publication(string defect, CoverageTableRefusal reason,
        int offset, int? group, int? entry, ulong? key)
    {
        byte[] bytes = Fixture();
        switch (defect)
        {
            case "magic": bytes[0] = 0; break;
            case "version": Put(bytes, 4, uint.MaxValue); break;
            case "groups": Put(bytes, 200, 0); bytes = [.. bytes.AsSpan(0, 212), .. bytes.AsSpan(236)]; break;
            case "group-count": Put(bytes, 200, uint.MaxValue); break;
            case "entry-count": Put(bytes, 204, uint.MaxValue); break;
            case "state-count": Put(bytes, 208, uint.MaxValue); break;
            case "product": Put(bytes, 200, 2000000000); Put(bytes, 204, 2000000000); break;
            case "payload": Put(bytes, 200, 500000000); break;
            case "representative": Put(bytes, 216, 0xfffffffe); break;
            case "infinity": Put(bytes, 224, 0x7f800000); break;
            case "nan": Put(bytes, 224, 0x7fc00001); break;
            case "negative-infinity": Put(bytes, 224, 0xff800000); break;
            case "negative": Put(bytes, 224, 0xbf800000); break;
            case "negative-subnormal": Put(bytes, 224, 0x80000001); break;
            case "duplicate": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(248), 0); break;
            case "unsorted": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(236), ulong.MaxValue); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(248), 0); break;
            case "entry-low": Put(bytes, 256, 0xfffffffe); break;
            case "entry-high": Put(bytes, 256, 2); break;
            case "trailing": bytes = [.. bytes, 0]; break;
        }
        byte[] before = (byte[])bytes.Clone();
        CoverageRecessionTable? published = null;
        CoverageTableDataException error = Assert.Throws<CoverageTableDataException>(() => published = CoverageRecessionTableBinary.Read(bytes, "malformed"));
        Assert.Null(published);
        Assert.IsAssignableFrom<MalformedDataException>(error);
        Assert.Equal(reason, error.Reason);
        Assert.Equal("malformed", error.Identity);
        Assert.Equal(offset, error.ByteOffset);
        Assert.Equal(group, error.GroupIndex);
        Assert.Equal(entry, error.EntryIndex);
        Assert.Equal(key.HasValue ? new CoverageStateKey(key.Value) : null, error.Key);
        if (defect == "version") Assert.Equal(uint.MaxValue, error.FormatVersion);
        if (defect == "representative") Assert.Equal(-2, error.Representative);
        if (reason is CoverageTableRefusal.NonFiniteRecession or CoverageTableRefusal.NegativeRecession)
            Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(224)), error.ScalarWord);
        if (key.HasValue) Assert.Equal(1, error.StateIndex);
        if (defect is "group-count" or "entry-count" or "state-count")
        {
            Assert.Equal(4294967295L, error.Count);
            Assert.Equal(defect == "group-count" ? "PositionGroupCount" : defect == "entry-count" ? "EntryCount" : "StateCount", error.MemberName);
        }
        if (reason == CoverageTableRefusal.LengthOverflow) Assert.Equal(Array.MaxLength, error.Limit);
        if (defect == "product") Assert.Null(error.Count);
        if (defect == "payload") Assert.Equal(6000000236L, error.Count);
        Assert.Equal(before, bytes);
        Assert.Equal(Fixture(), CoverageRecessionTableBinary.Write(CoverageRecessionTableBinary.Read(Fixture(), "control")));
    }

    [Theory]
    [InlineData(0, null, null, null)]
    [InlineData(3, null, null, null)]
    [InlineData(7, null, null, null)]
    [InlineData(199, null, null, null)]
    [InlineData(211, null, null, null)]
    [InlineData(217, 1, null, null)]
    [InlineData(227, 1, 0, null)]
    [InlineData(255, null, null, null)]
    [InlineData(259, null, null, ulong.MaxValue)]
    public void Truncation_reports_first_missing_byte_and_available_record_context(int length, int? group, int? entry, ulong? key)
    {
        CoverageTableDataException error = Assert.Throws<CoverageTableDataException>(() => CoverageRecessionTableBinary.Read(Fixture().AsSpan(0, length), "short"));
        Assert.Equal(CoverageTableRefusal.Truncated, error.Reason);
        Assert.Equal("short", error.Identity);
        Assert.Equal(length, error.ByteOffset);
        Assert.Equal(group, error.GroupIndex);
        Assert.Equal(entry, error.EntryIndex);
        Assert.Equal(key.HasValue ? new CoverageStateKey(key.Value) : null, error.Key);
        if (length >= 236) Assert.Equal(1, error.StateIndex);
        Assert.Equal(2, CoverageRecessionTableBinary.Read(Fixture(), "control").EntryCount);
    }

    // Built independently of the production writer. Two groups, two rows, two states.
    static byte[] Fixture()
    {
        byte[] bytes = new byte[260];
        "KERT"u8.CopyTo(bytes);
        Put(bytes, 4, 1);
        for (int digest = 0; digest < 6; digest++) bytes.AsSpan(8 + digest * 32, 32).Fill((byte)(digest + 1));
        Put(bytes, 200, 2);
        Put(bytes, 204, 2);
        Put(bytes, 208, 2);
        Put(bytes, 212, 0);
        Put(bytes, 216, uint.MaxValue);
        Put(bytes, 220, 0);
        Put(bytes, 224, 0x80000000);
        Put(bytes, 228, 1);
        Put(bytes, 232, 0x7f7fffff);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(236), 0);
        Put(bytes, 244, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(248), ulong.MaxValue);
        Put(bytes, 256, 1);
        return bytes;
    }

    static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
