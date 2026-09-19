using System;
using System.Buffers.Binary;
using KhaozEngine.Skills;
using Xunit;

namespace KhaozEngine.Tests.Foundation.Skills;

/// <summary>The twelve-byte curve section: the round trip, the layout a second implementation has to agree
/// with, and the refusals that keep a corrupted section from becoming a curve.</summary>
public class SkillXpCurveCodecTests
{
    [Fact]
    public void A_round_trip_rebuilds_the_same_curve()
    {
        byte[] section = SkillXpCurveCodec.Encode(TestSkills.Curve);

        Assert.Equal(SkillXpCurveCodec.Bytes, section.Length);
        Assert.True(SkillXpCurveCodec.TryDecode(section, out SkillXpCurve? back));
        // The HASH, because that is the identity a load compares before deciding whether to rescale.
        Assert.Equal(TestSkills.Curve.Hash, back.Hash);
        Assert.Equal(TestSkills.Curve.FirstLevelCost, back.FirstLevelCost);
        Assert.Equal(TestSkills.Curve.DoublingLevels, back.DoublingLevels);
        Assert.Equal(TestSkills.Curve.MaxLevel, back.MaxLevel);
        Assert.Equal(TestSkills.Curve.XpForLevel(100), back.XpForLevel(100));
    }

    [Fact]
    public void The_layout_is_three_little_endian_int32_in_a_fixed_order()
    {
        byte[] section = SkillXpCurveCodec.Encode(TestSkills.Curve);

        Assert.Equal(114, BinaryPrimitives.ReadInt32LittleEndian(section));
        Assert.Equal(6, BinaryPrimitives.ReadInt32LittleEndian(section.AsSpan(4)));
        Assert.Equal(100, BinaryPrimitives.ReadInt32LittleEndian(section.AsSpan(8)));
    }

    [Fact]
    public void A_zeroed_or_negative_section_is_unreadable_rather_than_a_curve()
    {
        // REBUILT rather than trusted. Without the rebuild a zeroed section builds a curve whose
        // thresholds are all zero and whose every level is the cap, which would move every level in the
        // book. Unreadable means the caller leaves the stored experience exactly as it is.
        Assert.False(SkillXpCurveCodec.TryDecode(new byte[SkillXpCurveCodec.Bytes], out SkillXpCurve? zeroed));
        Assert.Null(zeroed);

        Assert.False(SkillXpCurveCodec.TryDecode(Section(-1, 6, 100), out _));
        Assert.False(SkillXpCurveCodec.TryDecode(Section(114, 0, 100), out _));
        Assert.False(SkillXpCurveCodec.TryDecode(Section(114, 6, 1), out _));
        Assert.False(SkillXpCurveCodec.TryDecode(Section(114, 6, -100), out _));
    }

    [Fact]
    public void An_absurd_level_cap_is_refused_before_anything_is_allocated()
    {
        // A curve builds its thresholds eagerly, one double per level, so four corrupted bytes naming a
        // two-billion level cap would ask for sixteen gigabytes before anything else got a say.
        Assert.False(SkillXpCurveCodec.TryDecode(Section(114, 6, int.MaxValue), out _));
        Assert.False(SkillXpCurveCodec.TryDecode(Section(114, 6, SkillXpCurveCodec.MaxLevelCeiling + 1), out _));
        Assert.True(SkillXpCurveCodec.TryDecode(Section(114, 6, SkillXpCurveCodec.MaxLevelCeiling), out _));
    }

    [Fact]
    public void A_section_of_the_wrong_width_is_unreadable()
    {
        byte[] section = SkillXpCurveCodec.Encode(TestSkills.Curve);

        Assert.False(SkillXpCurveCodec.TryDecode(section.AsSpan(0, SkillXpCurveCodec.Bytes - 1), out _));
        Assert.False(SkillXpCurveCodec.TryDecode(new byte[SkillXpCurveCodec.Bytes + 1], out _));
        Assert.False(SkillXpCurveCodec.TryDecode(ReadOnlySpan<byte>.Empty, out _));
    }

    [Fact]
    public void The_classic_curve_is_stored_as_an_absent_section_rather_than_written()
    {
        // An ABSENT section means the classic curve, which is why its hash is a durable word. Writing one
        // for it would make the two indistinguishable, so Encode refuses rather than writing its zeroes.
        Assert.Throws<ArgumentException>(() => _ = SkillXpCurveCodec.Encode(SkillXpCurve.Osrs));
        Assert.Throws<ArgumentNullException>(() => _ = SkillXpCurveCodec.Encode(null!));
    }

    static byte[] Section(int firstLevelCost, int doublingLevels, int maxLevel)
    {
        byte[] bytes = new byte[SkillXpCurveCodec.Bytes];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, firstLevelCost);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), doublingLevels);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), maxLevel);
        return bytes;
    }
}
