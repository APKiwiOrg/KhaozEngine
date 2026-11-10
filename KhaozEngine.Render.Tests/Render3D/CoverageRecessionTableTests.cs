using System;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public class CoverageRecessionTableTests
{
    internal static CoverageRecessionIdentity Identity => new(
        new string('1', 64), new string('2', 64), new string('3', 64),
        new string('4', 64), new string('5', 64), new string('6', 64));

    [Fact]
    public void Scalar_rows_share_representatives_and_roundtrip_exact_words()
    {
        int[] representatives = Enumerable.Range(0, 958).ToArray();
        representatives[0] = -1;
        float[] scalars = new float[1916];
        uint[] words = [0, 0x80000000, 1, 0x007fffff, 0x00800000, 0x7f7fffff];
        for (int i = 0; i < scalars.Length; i++) scalars[i] = BitConverter.UInt32BitsToSingle(words[i % words.Length]);
        CoverageStateEntry[] states = [new(new(9), 1), new(new(0), 0), new(new(4), -1)];
        CoverageRecessionTable table = CoverageRecessionTable.Create(Identity, representatives, scalars, states);
        byte[] bytes = CoverageRecessionTableBinary.Write(table);

        // 212 header bytes, 958 representatives once, two scalar rows, three 12-byte states.
        Assert.Equal(11744, bytes.Length);
        Assert.Equal(3832, table.Entry(0).Length * sizeof(float));
        Assert.Equal(958, table.PositionGroupCount);
        Assert.Equal(2, table.EntryCount);
        representatives[1] = -9;
        scalars[0] = float.NaN;
        states[0] = new(new(9), -2);
        Assert.Equal(bytes, CoverageRecessionTableBinary.Write(table));

        CoverageRecessionTable loaded = CoverageRecessionTableBinary.Read(bytes, "synthetic-table");
        Array.Fill(bytes, (byte)0);
        Assert.Equal(Identity, loaded.Identity);
        Assert.Equal(-1, loaded.Representatives[0]);
        Assert.Equal(1, loaded.Representatives[1]);
        Assert.Equal(new ulong[] { 0, 4, 9 }, loaded.States.ToArray().Select(state => state.Key.Value));
        for (int entry = 0; entry < 2; entry++)
            for (int group = 0; group < 958; group++)
                Assert.Equal(words[(entry * 958 + group) % words.Length], BitConverter.SingleToUInt32Bits(loaded.Entry(entry)[group]));
    }

    [Fact]
    public void Key_lookup_distinguishes_entry_zero_from_holes()
    {
        CoverageRecessionTable table = CoverageRecessionTable.Create(Identity, [int.MaxValue], [0f, 1f],
            [new(new(ulong.MaxValue), 1), new(new(42), -1), new(new(0), 0)]);
        Assert.True(table.TrySelect(new(0), out int zero));
        Assert.Equal(0, zero);
        Assert.True(table.TrySelect(new(ulong.MaxValue), out int last));
        Assert.Equal(1, last);
        Assert.False(table.TrySelect(new(42), out int hole));
        Assert.Equal(-1, hole);
        Assert.False(table.TrySelect(new(43), out int missing));
        Assert.Equal(-1, missing);
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Entry(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Entry(2));

        CoverageRecessionTable allHoles = CoverageRecessionTable.Create(Identity, [-1], [], [new(new(0), -1)]);
        CoverageRecessionTable emptyIndex = CoverageRecessionTable.Create(Identity, [0], [], []);
        Assert.False(CoverageRecessionTableBinary.Read(CoverageRecessionTableBinary.Write(allHoles), "holes").TrySelect(new(0), out hole));
        Assert.Equal(-1, hole);
        Assert.False(emptyIndex.TrySelect(new(0), out missing));
        Assert.Equal(-1, missing);
    }

    [Theory]
    [InlineData("groups", CoverageTableRefusal.ZeroGroups, null, null)]
    [InlineData("rows", CoverageTableRefusal.IncompleteRows, 1, 0)]
    [InlineData("representative", CoverageTableRefusal.InvalidRepresentative, 1, null)]
    [InlineData("finite", CoverageTableRefusal.NonFiniteRecession, 1, 0)]
    [InlineData("negative", CoverageTableRefusal.NegativeRecession, 1, 0)]
    [InlineData("negative-subnormal", CoverageTableRefusal.NegativeRecession, 1, 0)]
    [InlineData("duplicate", CoverageTableRefusal.DuplicateKey, null, 0)]
    [InlineData("entry-low", CoverageTableRefusal.InvalidEntryIndex, null, -2)]
    [InlineData("entry-high", CoverageTableRefusal.InvalidEntryIndex, null, 1)]
    public void Programmatic_data_refuses_without_publishing(string defect, CoverageTableRefusal reason, int? group, int? entry)
    {
        int[] representatives = defect == "groups" ? [] : [0, defect == "representative" ? -2 : -1];
        float[] scalars = defect == "rows" ? [0] : [0, 1];
        if (defect == "finite") scalars[1] = float.PositiveInfinity;
        if (defect == "negative") scalars[1] = -1;
        if (defect == "negative-subnormal") scalars[1] = BitConverter.UInt32BitsToSingle(0x80000001);
        CoverageStateEntry[] states = [new(new(7), defect == "entry-low" ? -2 : defect == "entry-high" ? 1 : 0)];
        if (defect == "duplicate") states = [new(new(7), 0), new(new(7), 0)];
        CoverageRecessionTable? published = null;
        CoverageTableDataException error = Assert.Throws<CoverageTableDataException>(() =>
            published = CoverageRecessionTable.Create(Identity, representatives, scalars, states));
        Assert.Null(published);
        Assert.IsAssignableFrom<MalformedDataException>(error);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(group, error.GroupIndex);
        Assert.Equal(entry, error.EntryIndex);
        Assert.Null(error.Identity);
        Assert.Null(error.ByteOffset);
        if (defect is "duplicate" or "entry-low" or "entry-high") Assert.Equal(new CoverageStateKey(7), error.Key);

        Assert.Equal(1, CoverageRecessionTable.Create(Identity, [0, -1], [0, 1], [new(new(7), 0)]).EntryCount);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "")]
    [InlineData(2, "abc")]
    [InlineData(3, "ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd")]
    [InlineData(4, "g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData(5, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    public void Identity_requires_six_lowercase_sha256_digests(int member, string? invalid)
    {
        CoverageRecessionIdentity identity = Identity;
        string[] names = ["HostSha256", "HostMapSha256", "ConfigurationSha256", "RefinementSha256", "ArithmeticSha256", "StateSchemaSha256"];
        identity = member switch
        {
            0 => identity with { HostSha256 = invalid! },
            1 => identity with { HostMapSha256 = invalid! },
            2 => identity with { ConfigurationSha256 = invalid! },
            3 => identity with { RefinementSha256 = invalid! },
            4 => identity with { ArithmeticSha256 = invalid! },
            _ => identity with { StateSchemaSha256 = invalid! },
        };
        CoverageTableDataException error = Assert.Throws<CoverageTableDataException>(() => CoverageRecessionTable.Create(identity, [0], [0], []));
        Assert.Equal(CoverageTableRefusal.MalformedDigest, error.Reason);
        Assert.Equal(names[member], error.MemberName);
        Assert.Equal(Identity, CoverageRecessionTable.Create(Identity, [0], [0], []).Identity);
    }
}
