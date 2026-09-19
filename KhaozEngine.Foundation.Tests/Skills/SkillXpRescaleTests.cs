using System;
using KhaozEngine.Skills;
using Xunit;

namespace KhaozEngine.Tests.Foundation.Skills;

/// <summary>
/// The curve can move, so a saved character has to be carried across it. This is what says they are: the
/// level and the progress past it survive a curve change in both directions.
/// </summary>
/// <remarks>
/// The bug this closes is not hypothetical and is not one edit: a game tunes its curve, and the first tune
/// on a real one put a fresh character's starting experience three levels lower, so they would have logged
/// in weaker having lost nothing they could see. Every later tune would do the same thing again.
/// </remarks>
public class SkillXpRescaleTests
{
    static SkillXpCurve Owner => TestSkills.Curve;

    [Fact]
    public void Every_level_survives_the_trip_in_both_directions()
    {
        SkillXpCurve osrs = SkillXpCurve.Osrs;
        SkillXpCurve owner = Owner;

        // The two caps differ (99 against 100), so the expectation is the level itself everywhere except
        // at a cap: a character sitting on the top of the old table arrives on the top of the new one,
        // because the alternative leaves a maxed character one short of the cap and reading that as a
        // demotion is fair.
        for (int level = 1; level <= 100; level++)
        {
            if (level <= osrs.MaxLevel)
            {
                double carried = SkillXpRescale.Preserve(osrs, owner, osrs.XpForLevel(level));
                int expected = level >= osrs.MaxLevel ? owner.MaxLevel : level;
                Assert.Equal(expected, owner.LevelFor(carried));
            }
            if (level <= owner.MaxLevel)
            {
                double back = SkillXpRescale.Preserve(owner, osrs, owner.XpForLevel(level));
                int expected = level >= owner.MaxLevel ? osrs.MaxLevel : Math.Min(level, osrs.MaxLevel);
                Assert.Equal(expected, osrs.LevelFor(back));
            }
        }
    }

    [Fact]
    public void The_fraction_toward_the_next_level_survives_to_a_sixth_decimal()
    {
        SkillXpCurve osrs = SkillXpCurve.Osrs;
        SkillXpCurve owner = Owner;

        // A level's worth of grind is a real thing to lose, so the progress bar has to land where it was.
        // The sweep is every level and several points inside each, including the two ends of the band.
        double[] parts = [0d, 0.001d, 0.25d, 0.5d, 0.75d, 0.999d];
        for (int level = 1; level < osrs.MaxLevel; level++)
        {
            double floor = osrs.XpForLevel(level);
            double span = osrs.XpForLevel(level + 1) - floor;
            foreach (double part in parts)
            {
                double carried = SkillXpRescale.Preserve(osrs, owner, floor + (part * span));
                Assert.Equal(level, owner.LevelFor(carried));
                double toFloor = owner.XpForLevel(level);
                double toSpan = owner.XpForLevel(level + 1) - toFloor;
                Assert.Equal(part, (carried - toFloor) / toSpan, 6);
            }
        }
    }

    [Fact]
    public void Max_stays_max_and_nothing_stays_nothing()
    {
        SkillXpCurve osrs = SkillXpCurve.Osrs;
        SkillXpCurve owner = Owner;
        SkillXpCurve shortened = SkillXpCurve.Configured(114, 6, 50);

        // The top of a table has no next level to hold a fraction of, so a maxed skill arrives maxed.
        Assert.Equal(owner.XpForLevel(owner.MaxLevel),
            SkillXpRescale.Preserve(osrs, owner, osrs.XpForLevel(osrs.MaxLevel)));
        Assert.Equal(osrs.XpForLevel(osrs.MaxLevel),
            SkillXpRescale.Preserve(owner, osrs, owner.XpForLevel(owner.MaxLevel)));

        // And a cap that came DOWN pins the character to the new top rather than dropping them to a level
        // the number happens to land on, which is the only direction that takes nothing away.
        Assert.Equal(shortened.MaxLevel,
            shortened.LevelFor(SkillXpRescale.Preserve(osrs, shortened, osrs.XpForLevel(90))));
        Assert.Equal(shortened.XpForLevel(shortened.MaxLevel),
            SkillXpRescale.Preserve(osrs, shortened, osrs.XpForLevel(90)));

        // Nothing earned is nothing earned under any curve, and a stored NaN is not a level to preserve.
        Assert.Equal(0d, SkillXpRescale.Preserve(osrs, owner, 0d));
        Assert.Equal(0d, SkillXpRescale.Preserve(osrs, owner, -5d));
        Assert.Equal(0d, SkillXpRescale.Preserve(osrs, owner, double.NaN));
        Assert.Equal(0d, SkillXpRescale.Preserve(osrs, owner, double.PositiveInfinity));
    }

    [Fact]
    public void A_curve_that_did_not_move_moves_nobody()
    {
        SkillXpCurve owner = Owner;
        for (int level = 1; level <= owner.MaxLevel; level++)
        {
            double xp = owner.XpForLevel(level);
            Assert.Equal(xp, SkillXpRescale.Preserve(owner, owner, xp));
        }
        // Which is what a caller leans on: it compares the hashes and only rebases when they differ.
        Assert.Equal(owner.Hash,
            SkillXpCurve.Configured(owner.FirstLevelCost, owner.DoublingLevels, owner.MaxLevel).Hash);
        Assert.NotEqual(owner.Hash, SkillXpCurve.Osrs.Hash);
    }

    [Fact]
    public void A_rescale_never_hands_out_a_free_level()
    {
        // A fraction a whisker under one, times a span in the tens of millions, can round up onto the next
        // threshold. Held one bit below it, because the contract is the LEVEL first and the fraction
        // second, so the sweep over every level under both caps has to stay on its own level.
        SkillXpCurve osrs = SkillXpCurve.Osrs;
        SkillXpCurve owner = Owner;
        for (int level = 1; level < Math.Min(osrs.MaxLevel, owner.MaxLevel); level++)
        {
            double justUnder = Math.BitDecrement(osrs.XpForLevel(level + 1));
            Assert.Equal(level, owner.LevelFor(SkillXpRescale.Preserve(osrs, owner, justUnder)));
        }
    }

    [Fact]
    public void Rebase_walks_a_whole_book_and_leaves_the_untrained_alone()
    {
        SkillBook book = TestSkills.Fresh();
        book.SetXp(TestSkills.Chopping, SkillXpCurve.Osrs.XpForLevel(20));
        // A locked skill can hold experience from a decoded record, and it is rebased like any other:
        // leaving it under the old curve would misprice it the moment the roster opens again.
        book.SetXp(TestSkills.Weaving, SkillXpCurve.Osrs.XpForLevel(15));

        SkillXpRescale.Rebase(book, SkillXpCurve.Osrs, TestSkills.Curve);

        Assert.Equal(20, book.Level(TestSkills.Chopping));
        Assert.Equal(15, book.Level(TestSkills.Weaving));
        // An untrained skill is left exactly alone rather than written back as a computed zero, so a
        // rebase over an untouched book is a no-op down to the stored bytes.
        Assert.Equal(0d, book.Xp(TestSkills.Striking));
        Assert.Equal(0d, book.Xp(TestSkills.Digging));
    }

    [Fact]
    public void A_record_saved_under_the_classic_curve_reads_back_on_the_level_it_was_left_on()
    {
        // The whole point, end to end, in the shape a game's record codec has: the blob stores numbers, the
        // record stores which curve they were under, and a load that finds a different one rebases. 1154 is
        // the classic table's level 10, which is what a character saved before a configurable curve
        // carries. Read back it has to still be level 10, which this curve prices at 1702, not the level
        // the raw number now buys.
        SkillBook saved = TestSkills.Empty();
        saved.SetXp(TestSkills.Vitality, 1154d);
        Assert.Equal(10, SkillXpCurve.Osrs.LevelFor(1154d));
        Assert.Equal(7, TestSkills.Curve.LevelFor(1154d));

        byte[] blob = SkillBookCodec.Encode(saved);
        Assert.True(SkillBookCodec.TryDecode(blob, TestSkills.Roster, TestSkills.Curve, out SkillBook back));
        SkillXpRescale.Rebase(back, SkillXpCurve.Osrs, TestSkills.Curve);

        Assert.Equal(10, back.Level(TestSkills.Vitality));
        Assert.Equal(1702d, back.Xp(TestSkills.Vitality), 6);
        Assert.Equal(TestSkills.Curve.XpForLevel(10), back.Xp(TestSkills.Vitality), 6);
    }

    [Fact]
    public void Rescale_needs_its_curves()
    {
        Assert.Throws<ArgumentNullException>(() => _ = SkillXpRescale.Preserve(null!, Owner, 5d));
        Assert.Throws<ArgumentNullException>(() => _ = SkillXpRescale.Preserve(Owner, null!, 5d));
        Assert.Throws<ArgumentNullException>(() => SkillXpRescale.Rebase(null!, Owner, Owner));
        Assert.Throws<ArgumentNullException>(() => SkillXpRescale.Rebase(TestSkills.Fresh(), null!, Owner));
    }
}
