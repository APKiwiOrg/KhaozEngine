using System;
using KhaozEngine.Skills;
using Xunit;

namespace KhaozEngine.Tests.Foundation.Skills;

/// <summary>
/// Bytes written by another codebase, read here. The package was lifted out of a game that already has saved
/// characters, and the whole reason it exists is that those saves keep decoding once that game swaps its own
/// codec for this one. Every other codec test builds its input from this codec's own constants, which proves
/// the codec agrees with itself. These prove it agrees with the bytes that are already on disk.
/// </summary>
/// <remarks>
/// The literals below were captured on 2026-09-19 by running the ORIGINAL encoders, not by reading their
/// source: a 23-skill book under a first level costing 114, doubling every six, capped at 100, holding 1154,
/// 2500.5, 123456.75, 200,000,000 (the ceiling) and 0.125 at ids 0, 1, 4, 11 and 22, and that curve's own
/// twelve-byte section. Do not regenerate them from this codec. If one of these fails, this codec changed the
/// persisted format and every existing save is at risk, which is a major version and a migration, not a test
/// to update.
/// </remarks>
public class SkillGoldenBytesTests
{
    const string BookHex =
        "0217" +
        "00000000000008924001000000000089A340020000000000000000030000000000000000" +
        "04000000000C24FE40050000000000000000060000000000000000070000000000000000" +
        "0800000000000000000900000000000000000A00000000000000000B0000000084D7A741" +
        "0C00000000000000000D00000000000000000E00000000000000000F0000000000000000" +
        "100000000000000000110000000000000000120000000000000000130000000000000000" +
        "14000000000000000015000000000000000016000000000000C03F";

    const string CurveHex = "720000000600000064000000";

    const string CurveHash = "21e95e5cf1537601";

    const int SkillCount = 23;

    static SkillXpCurve Curve => SkillXpCurve.Configured(114, 6, 100);

    [Fact]
    public void A_book_written_by_the_original_encoder_decodes_to_the_values_it_was_given()
    {
        byte[] blob = Convert.FromHexString(BookHex);
        Assert.Equal(SkillBookCodec.HeaderBytes + SkillCount * SkillBookCodec.EntryBytes, blob.Length);
        Assert.Null(SkillBookCodec.Validate(blob, requiredIndex: 0));

        Assert.True(SkillBookCodec.TryDecode(blob, SkillRoster.Flat(SkillCount), Curve, out SkillBook book,
            requiredIndex: 0));

        Assert.Equal(1154d, book.Xp(0));
        Assert.Equal(2500.5d, book.Xp(1));
        Assert.Equal(123456.75d, book.Xp(4));
        Assert.Equal(SkillXpLimits.MaxXp, book.Xp(11));
        Assert.Equal(0.125d, book.Xp(22));
        for (int i = 0; i < SkillCount; i++)
            if (i is not (0 or 1 or 4 or 11 or 22))
                Assert.Equal(0d, book.Xp(i));
    }

    [Fact]
    public void Encoding_that_book_again_reproduces_the_original_bytes_exactly()
    {
        // The other direction. The game that adopts this package may still have an older build reading the
        // same store during a rollout, so what this codec WRITES has to be what the original reader accepts,
        // and byte equality with the original writer is the strongest form of that.
        byte[] original = Convert.FromHexString(BookHex);
        Assert.True(SkillBookCodec.TryDecode(original, SkillRoster.Flat(SkillCount), Curve, out SkillBook book));

        Assert.Equal(original, SkillBookCodec.Encode(book));
    }

    [Fact]
    public void A_curve_section_written_by_the_original_encoder_rebuilds_the_same_curve()
    {
        byte[] section = Convert.FromHexString(CurveHex);
        Assert.Equal(SkillXpCurveCodec.Bytes, section.Length);

        Assert.True(SkillXpCurveCodec.TryDecode(section, out SkillXpCurve? curve));

        Assert.Equal(114, curve.FirstLevelCost);
        Assert.Equal(6, curve.DoublingLevels);
        Assert.Equal(100, curve.MaxLevel);
        Assert.Equal(section, SkillXpCurveCodec.Encode(curve));
    }

    [Fact]
    public void The_curve_hash_is_the_one_the_original_computed()
    {
        // A record stores the hash of the curve it was written under and compares it on load to decide
        // whether to reprice. A hash that drifted by one character would reprice every character on the
        // first load after adoption, silently and irreversibly.
        Assert.Equal(CurveHash, Curve.Hash);
    }
}
