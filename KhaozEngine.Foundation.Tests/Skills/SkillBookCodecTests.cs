using System;
using System.Buffers.Binary;
using System.Globalization;
using KhaozEngine.Skills;
using Xunit;

namespace KhaozEngine.Tests.Foundation.Skills;

/// <summary>
/// The durable form of a book, which a store keeps verbatim and never interprets. The validation half is
/// what decides whether a bad blob quarantines a record or silently resets a character's levels, so every
/// malformed shape gets its own case.
/// </summary>
public class SkillBookCodecTests
{
    const int HeaderBytes = SkillBookCodec.HeaderBytes;
    const int EntryBytes = SkillBookCodec.EntryBytes;

    static SkillBook BookAt(double vitalityXp)
    {
        SkillBook book = TestSkills.Empty();
        book.SetXp(TestSkills.Vitality, vitalityXp);
        return book;
    }

    static bool Decode(byte[]? blob, out SkillBook book) =>
        SkillBookCodec.TryDecode(blob, TestSkills.Roster, TestSkills.Curve, out book);

    [Fact]
    public void A_round_trip_is_exact()
    {
        byte[] blob = SkillBookCodec.Encode(BookAt(5000d));

        Assert.True(Decode(blob, out SkillBook decoded));
        // Doubles go on the wire whole, so this is an equality assertion rather than a tolerance one.
        Assert.Equal(5000d, decoded.Xp(TestSkills.Vitality));
    }

    [Fact]
    public void A_round_trip_carries_every_skill_in_the_roster()
    {
        SkillBook book = TestSkills.Fresh();
        book.SetXp(TestSkills.Striking, 83d);
        book.SetXp(TestSkills.Chopping, 174d);
        book.SetXp(TestSkills.Weaving, 12d);   // locked, and still stored: a lock is about awards

        Assert.True(Decode(SkillBookCodec.Encode(book), out SkillBook round));
        for (int skill = 0; skill < book.Count; skill++)
            Assert.Equal(book.Xp(skill), round.Xp(skill));
    }

    [Fact]
    public void A_null_or_empty_blob_is_no_state_rather_than_a_fault()
    {
        Assert.Null(SkillBookCodec.Validate(null));
        Assert.Null(SkillBookCodec.Validate([]));

        Assert.False(Decode(null, out _));
        Assert.False(Decode([], out _));
    }

    [Fact]
    public void Validate_rejects_an_unknown_version_by_number()
    {
        byte[] blob = SkillBookCodec.Encode(BookAt(5000d));
        // Version 1 belonged to a game's own pre-extraction roster and is NOT read here, so the refusal
        // names the found version and the one this build reads. There is no legacy reader hook: a game
        // with older bytes dispatches on VersionOf, migrates them, and hands a version 2 blob back.
        blob[0] = 1;

        string? reason = SkillBookCodec.Validate(blob);

        Assert.NotNull(reason);
        Assert.Contains("1", reason, StringComparison.Ordinal);
        Assert.Contains(SkillBookCodec.Version.ToString(CultureInfo.InvariantCulture), reason,
            StringComparison.Ordinal);
        Assert.False(Decode(blob, out _));
    }

    [Fact]
    public void VersionOf_is_the_hook_a_game_with_older_bytes_dispatches_on()
    {
        Assert.Equal(SkillBookCodec.Version, SkillBookCodec.VersionOf(SkillBookCodec.Encode(BookAt(5d))));
        Assert.Equal(1, SkillBookCodec.VersionOf([1, 0]));
        // Nothing to read a version out of, said the same way for both shapes.
        Assert.Equal(-1, SkillBookCodec.VersionOf([]));
        Assert.Equal(-1, SkillBookCodec.VersionOf([SkillBookCodec.Version]));
    }

    [Fact]
    public void The_version_never_lands_in_the_reserved_band()
    {
        // A composite record format that wraps a skill blob tells the two apart by byte 0, so the top band
        // is reserved fleet-wide. A version assigned into it would read every bare skill blob as a
        // malformed composite and quarantine it.
        Assert.True(SkillBookCodec.Version < SkillBookCodec.ReservedVersionFloor,
            $"version {SkillBookCodec.Version} is inside the reserved band");
        Assert.Equal(0xF0, SkillBookCodec.ReservedVersionFloor);
    }

    [Fact]
    public void Validate_rejects_a_truncated_blob()
    {
        byte[] full = SkillBookCodec.Encode(BookAt(5000d));

        Assert.NotNull(SkillBookCodec.Validate(full[..^1]));
        Assert.NotNull(SkillBookCodec.Validate([SkillBookCodec.Version]));
    }

    [Fact]
    public void Validate_rejects_a_non_finite_experience()
    {
        byte[] blob = SkillBookCodec.Encode(BookAt(5000d));
        // Straight over the first entry's payload, past its id byte, because SetXp refuses to store one
        // and the only way a NaN reaches a store is a corrupted or hand-edited record.
        BinaryPrimitives.WriteDoubleLittleEndian(blob.AsSpan(HeaderBytes + 1), double.NaN);

        string? reason = SkillBookCodec.Validate(blob);

        Assert.NotNull(reason);
        Assert.Contains("non-finite", reason, StringComparison.Ordinal);
        Assert.False(Decode(blob, out _));
    }

    [Fact]
    public void Validate_refuses_a_negative_experience_for_being_negative()
    {
        byte[] blob = SkillBookCodec.Encode(BookAt(5000d));
        BinaryPrimitives.WriteDoubleLittleEndian(blob.AsSpan(HeaderBytes + 1), -5d);

        string? reason = SkillBookCodec.Validate(blob);

        // The REASON, not merely the refusal. This string is what a quarantine log line carries, and a
        // finite negative called non-finite sends whoever reads it hunting a NaN that is not there.
        Assert.NotNull(reason);
        Assert.DoesNotContain("non-finite", reason, StringComparison.Ordinal);
        Assert.Contains("-5", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_an_experience_over_the_ceiling()
    {
        byte[] blob = SkillBookCodec.Encode(BookAt(5000d));
        // Finite, non-negative and absurd, which is what makes it the dangerous shape: without a ceiling
        // check it passes validation, SetXp clamps it on the way in, and a corrupted record comes back as
        // a maxed character rather than being quarantined.
        BinaryPrimitives.WriteDoubleLittleEndian(blob.AsSpan(HeaderBytes + 1), 1e300d);

        string? reason = SkillBookCodec.Validate(blob);

        Assert.NotNull(reason);
        Assert.Contains(SkillXpLimits.MaxXp.ToString(CultureInfo.InvariantCulture), reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_a_blob_carrying_one_skill_twice()
    {
        // Not producible by Encode, which writes each id once and in order. Reachable by corruption, and
        // the shape matters because the decoder takes the LAST entry for a skill: a duplicate is a record
        // that reads as well-formed while quietly carrying two answers to the same question, and the one
        // it keeps depends on nothing a reader can see.
        byte[] blob = Blob((TestSkills.Vitality, 5000d), (TestSkills.Vitality, 90d));

        Assert.NotNull(SkillBookCodec.Validate(blob));
        Assert.False(Decode(blob, out _));
    }

    [Fact]
    public void Validate_rejects_a_repeated_unknown_id_too()
    {
        // Over-strict on purpose: the decoder skips an unknown id whole, so the two-answers argument does
        // not cover this one, and a blob naming any id twice is malformed whether or not this build reads
        // that id.
        byte[] blob = Blob((TestSkills.Vitality, 1d), (200, 5d), (200, 6d));

        Assert.NotNull(SkillBookCodec.Validate(blob));
    }

    [Fact]
    public void An_unknown_higher_id_is_skipped_rather_than_failing()
    {
        // The forward-compatible half of the count rule: a blob written by a build with more skills than
        // this one decodes clean, because the entry is fixed width and reading past it is exact. A player
        // who levelled a skill on a newer build is not quarantined by an older one.
        byte[] blob = Blob((TestSkills.Vitality, 5000d), (TestSkills.Count, 777d), (200, 1234d));

        Assert.Null(SkillBookCodec.Validate(blob));
        Assert.True(Decode(blob, out SkillBook decoded));
        Assert.Equal(5000d, decoded.Xp(TestSkills.Vitality));
        Assert.Equal(TestSkills.Count, decoded.Count);
    }

    [Fact]
    public void A_narrower_old_blob_decodes_with_the_new_skills_at_zero()
    {
        // The backward half of the same rule, and the reason a count change needs no version bump: a blob
        // written when the roster was three wide reads into today's, keyed by id, with everything appended
        // since at zero.
        byte[] blob = Blob((TestSkills.Vitality, 1500d), (TestSkills.Combat, 0d), (TestSkills.Gathering, 0d));

        Assert.Null(SkillBookCodec.Validate(blob));
        Assert.True(Decode(blob, out SkillBook decoded));
        Assert.Equal(1500d, decoded.Xp(TestSkills.Vitality));
        Assert.Equal(0d, decoded.Xp(TestSkills.Chopping));
        Assert.Equal(1, decoded.Level(TestSkills.Chopping));
        // And a re-save widens the blob to today's count without a version bump.
        byte[] rewritten = SkillBookCodec.Encode(decoded);
        Assert.Equal(SkillBookCodec.Version, rewritten[0]);
        Assert.Equal(TestSkills.Count, rewritten[1]);
    }

    [Fact]
    public void A_required_index_refuses_a_blob_that_lost_it()
    {
        // A count of zero is a legal LENGTH (the header and nothing after it) and every other check passes
        // it, but the book it decodes to is a level one character wearing someone else's record, because
        // the decoder seeds an empty book and cannot tell a missing entry from a stored zero.
        Assert.NotNull(SkillBookCodec.Validate([SkillBookCodec.Version, 0], TestSkills.Vitality));

        // The same hole, reached the way the skip rule actually makes it reachable: a blob whose every
        // entry names a skill this build does not have.
        byte[] unknownOnly = Blob((200, 1234d));
        Assert.NotNull(SkillBookCodec.Validate(unknownOnly, TestSkills.Vitality));
        Assert.False(SkillBookCodec.TryDecode(unknownOnly, TestSkills.Roster, TestSkills.Curve, out _,
            TestSkills.Vitality));

        // And with no required index there is no such rule, which is the default.
        Assert.Null(SkillBookCodec.Validate(unknownOnly));
        Assert.True(Decode(unknownOnly, out _));
    }

    [Fact]
    public void A_required_index_above_a_byte_is_a_caller_bug()
    {
        // It fails LOUD rather than as a permanent quarantine: an id is one byte, so a required index
        // above that can never be found and every blob in the store would be refused.
        byte[] blob = SkillBookCodec.Encode(BookAt(5000d));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = SkillBookCodec.Validate(blob, 256));
    }

    [Fact]
    public void Encode_writes_the_current_version_and_the_whole_roster()
    {
        byte[] blob = SkillBookCodec.Encode(TestSkills.Fresh());

        Assert.Equal(SkillBookCodec.Version, blob[0]);
        Assert.Equal(TestSkills.Count, blob[1]);
        Assert.Equal(HeaderBytes + (TestSkills.Count * EntryBytes), blob.Length);
        // Ids ascend and the experience is little-endian, which is the layout a second implementation has
        // to agree with byte for byte.
        Assert.Equal(TestSkills.Vitality, blob[HeaderBytes]);
        Assert.Equal(TestSkills.Curve.XpForLevel(TestSkills.SeededLevel),
            BinaryPrimitives.ReadDoubleLittleEndian(blob.AsSpan(HeaderBytes + 1)));
    }

    [Fact]
    public void Encode_refuses_a_roster_wider_than_a_byte()
    {
        // Refused here rather than truncated into a blob a reader would decode as a different character.
        var book = new SkillBook(SkillRoster.Flat(SkillBookCodec.MaxCount + 1), TestSkills.Curve);

        Assert.Throws<ArgumentOutOfRangeException>(() => SkillBookCodec.Encode(book));
        Assert.Equal(SkillBookCodec.MaxCount, SkillRoster.Flat(SkillBookCodec.MaxCount).Count);
    }

    [Fact]
    public void Decode_needs_a_roster_and_a_curve()
    {
        byte[] blob = SkillBookCodec.Encode(BookAt(5000d));

        Assert.Throws<ArgumentNullException>(() =>
            _ = SkillBookCodec.TryDecode(blob, null!, TestSkills.Curve, out _));
        Assert.Throws<ArgumentNullException>(() =>
            _ = SkillBookCodec.TryDecode(blob, TestSkills.Roster, null!, out _));
        Assert.Throws<ArgumentNullException>(() => _ = SkillBookCodec.Encode(null!));
    }

    // A hand-built blob, because Encode can only write the ids this build has and half of these cases are
    // about the ones it cannot.
    static byte[] Blob(params (int Skill, double Xp)[] entries)
    {
        byte[] blob = new byte[HeaderBytes + (entries.Length * EntryBytes)];
        blob[0] = SkillBookCodec.Version;
        blob[1] = (byte)entries.Length;
        for (int i = 0; i < entries.Length; i++)
        {
            int at = HeaderBytes + (i * EntryBytes);
            blob[at] = (byte)entries[i].Skill;
            BinaryPrimitives.WriteDoubleLittleEndian(blob.AsSpan(at + 1), entries[i].Xp);
        }
        return blob;
    }
}
