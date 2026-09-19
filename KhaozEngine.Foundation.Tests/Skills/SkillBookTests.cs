using System;
using KhaozEngine.Skills;
using Xunit;

namespace KhaozEngine.Tests.Foundation.Skills;

/// <summary>One character's experience and the levels it buys: the seed, the two doors that move a number,
/// the lock rule on both of them, and the ceiling.</summary>
public class SkillBookTests
{
    [Fact]
    public void A_fresh_book_starts_at_the_seeded_level()
    {
        SkillBook book = TestSkills.Fresh();

        Assert.Equal(TestSkills.SeededLevel, book.Level(TestSkills.Vitality));
        // Level 10 is 1702 on 114 doubling every six, hand-checkable against the curve's own numbers.
        Assert.Equal(1702d, book.Xp(TestSkills.Vitality));
        // And nothing else was seeded, so the rest of the roster is a level one character.
        Assert.Equal(0d, book.Xp(TestSkills.Striking));
        Assert.Equal(1, book.Level(TestSkills.Striking));
    }

    [Fact]
    public void A_seed_is_priced_through_the_curve_rather_than_stored_as_a_number()
    {
        // The same seed under a steeper curve is a different NUMBER at the same level, which is the whole
        // reason a seed is a level: a game that tuned its curve reprices every fresh character for free.
        SkillXpCurve steeper = SkillXpCurve.Configured(TestSkills.Curve.FirstLevelCost * 4, 6, 100);
        SkillBook book = SkillBook.Fresh(TestSkills.Roster, steeper,
            new SkillSeed(TestSkills.Vitality, TestSkills.SeededLevel));

        Assert.Equal(TestSkills.SeededLevel, book.Level(TestSkills.Vitality));
        Assert.Equal(steeper.XpForLevel(TestSkills.SeededLevel), book.Xp(TestSkills.Vitality));
        Assert.NotEqual(TestSkills.Curve.XpForLevel(TestSkills.SeededLevel), book.Xp(TestSkills.Vitality));
    }

    [Fact]
    public void A_seed_is_written_past_the_lock_rule()
    {
        // A starting level on a skill no system has opened yet is a design statement, and the lock is about
        // AWARDS. Seeded, the number is there, and the award door still refuses to move it.
        SkillBook book = SkillBook.Fresh(TestSkills.Roster, TestSkills.Curve,
            new SkillSeed(TestSkills.Weaving, 5));

        Assert.Equal(5, book.Level(TestSkills.Weaving));
        Assert.False(book.AddXp(TestSkills.Weaving, 10_000d));
        Assert.Equal(TestSkills.Curve.XpForLevel(5), book.Xp(TestSkills.Weaving));
    }

    [Fact]
    public void A_seed_outside_the_roster_is_a_caller_bug()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SkillBook.Fresh(TestSkills.Roster, TestSkills.Curve, new SkillSeed(TestSkills.Count, 5)));
    }

    [Fact]
    public void AddXp_reports_only_the_award_that_crossed_a_level()
    {
        SkillBook book = TestSkills.Fresh();

        // Level 11 is 2024, so 1702 plus 100 is still level 10 and the next 300 takes it over.
        Assert.False(book.AddXp(TestSkills.Vitality, 100d));
        Assert.Equal(10, book.Level(TestSkills.Vitality));

        Assert.True(book.AddXp(TestSkills.Vitality, 300d));
        Assert.Equal(11, book.Level(TestSkills.Vitality));
        Assert.Equal(2102d, book.Xp(TestSkills.Vitality));
    }

    [Fact]
    public void A_level_arrives_on_the_award_the_arithmetic_says_it_does()
    {
        // The fraction has to ACCUMULATE rather than being rounded per award, and the only way to see that
        // is to count awards up to a level. Level 11 is 2024, a fresh book sits on 1702, so the gap is 322
        // and a 1.33 award pays it: 242 of them reach 2023.86 and the 243rd crosses. A book that rounded
        // each award to one would still be 80 short here.
        const double fractional = 1.33d;
        SkillBook book = TestSkills.Fresh();

        for (int i = 0; i < 242; i++)
            Assert.False(book.AddXp(TestSkills.Vitality, fractional),
                $"award {i + 1} crossed a level early, on {book.Xp(TestSkills.Vitality)}");

        Assert.Equal(10, book.Level(TestSkills.Vitality));
        Assert.Equal(2023.86d, book.Xp(TestSkills.Vitality), 6);

        Assert.True(book.AddXp(TestSkills.Vitality, fractional), "the 243rd award did not cross into 11");
        Assert.Equal(11, book.Level(TestSkills.Vitality));
    }

    [Fact]
    public void A_locked_skill_refuses_every_award()
    {
        SkillBook book = TestSkills.Fresh();

        for (int skill = 0; skill < book.Count; skill++)
        {
            if (!TestSkills.Roster.IsLocked(skill)) continue;
            // Refused, and refused SILENTLY as far as the book is concerned: nothing stored, and false
            // back, which is the same answer a zero award gives, so no caller has to learn a second
            // failure shape.
            Assert.False(book.AddXp(skill, 10_000d), $"skill {skill} is locked and took experience");
            Assert.Equal(0d, book.Xp(skill));
            Assert.Equal(1, book.Level(skill));
        }

        // The live ones take it, so this is a lock rather than a book that stopped storing.
        Assert.True(book.AddXp(TestSkills.Striking, 10_000d));
        Assert.True(book.AddXp(TestSkills.Chopping, 10_000d));
        Assert.True(book.AddXp(TestSkills.Vitality, 10_000d));
    }

    [Fact]
    public void A_non_positive_or_non_finite_award_is_dropped_rather_than_stored()
    {
        SkillBook book = TestSkills.Fresh();

        Assert.False(book.AddXp(TestSkills.Vitality, 0d));
        Assert.False(book.AddXp(TestSkills.Vitality, -50d));
        Assert.False(book.AddXp(TestSkills.Vitality, double.NaN));
        Assert.False(book.AddXp(TestSkills.Vitality, double.PositiveInfinity));
        Assert.Equal(1702d, book.Xp(TestSkills.Vitality));
    }

    [Fact]
    public void Experience_stops_at_the_ceiling()
    {
        SkillBook book = TestSkills.Fresh();
        book.SetXp(TestSkills.Vitality, SkillXpLimits.MaxXp);

        Assert.False(book.AddXp(TestSkills.Vitality, 1_000d));
        Assert.Equal(SkillXpLimits.MaxXp, book.Xp(TestSkills.Vitality));

        // The write door saturates too, rather than storing a number the codec would then refuse.
        book.SetXp(TestSkills.Chopping, SkillXpLimits.MaxXp * 2d);
        Assert.Equal(SkillXpLimits.MaxXp, book.Xp(TestSkills.Chopping));
    }

    [Fact]
    public void RemoveXp_reports_only_the_removal_that_dropped_a_level()
    {
        SkillBook book = TestSkills.Fresh();
        book.AddXp(TestSkills.Vitality, 400d);
        Assert.Equal(11, book.Level(TestSkills.Vitality));

        // 2102 less 50 is still over level 11's 2024, and the next 100 takes it under.
        Assert.False(book.RemoveXp(TestSkills.Vitality, 50d));
        Assert.Equal(11, book.Level(TestSkills.Vitality));
        Assert.Equal(2052d, book.Xp(TestSkills.Vitality));

        Assert.True(book.RemoveXp(TestSkills.Vitality, 100d));
        Assert.Equal(10, book.Level(TestSkills.Vitality));
        Assert.Equal(1952d, book.Xp(TestSkills.Vitality));
    }

    [Fact]
    public void RemoveXp_floors_at_zero()
    {
        SkillBook book = TestSkills.Fresh();

        Assert.True(book.RemoveXp(TestSkills.Vitality, 1_000_000d));
        Assert.Equal(0d, book.Xp(TestSkills.Vitality));
        Assert.Equal(1, book.Level(TestSkills.Vitality));

        // Already on the floor: nothing to lose, no level dropped, and still never negative.
        Assert.False(book.RemoveXp(TestSkills.Vitality, 5d));
        Assert.Equal(0d, book.Xp(TestSkills.Vitality));
    }

    [Fact]
    public void A_locked_skill_refuses_every_removal()
    {
        SkillBook book = TestSkills.Fresh();

        for (int skill = 0; skill < book.Count; skill++)
        {
            if (!TestSkills.Roster.IsLocked(skill)) continue;
            // A decoded book can hold experience in a locked skill, so the guard is proven against a
            // non-zero total rather than against a floor that could not move anyway.
            book.SetXp(skill, 10_000d);
            Assert.False(book.RemoveXp(skill, 10_000d), $"skill {skill} is locked and lost experience");
            Assert.Equal(10_000d, book.Xp(skill));
        }
    }

    [Fact]
    public void A_book_levels_off_the_curve_it_carries_and_reads_no_ambient_holder()
    {
        // Two books under two curves holding the SAME experience is the case that separates a carried curve
        // from a facade over whatever was published last, because a static table would answer both of them
        // the same level.
        SkillXpCurve shipped = TestSkills.Curve;
        SkillXpCurve steeper = SkillXpCurve.Configured(
            shipped.FirstLevelCost * 4, shipped.DoublingLevels, shipped.MaxLevel);
        Assert.NotEqual(shipped.Hash, steeper.Hash);

        double atTen = shipped.XpForLevel(10);
        var onShipped = new SkillBook(TestSkills.Roster, shipped);
        var onSteeper = new SkillBook(TestSkills.Roster, steeper);
        onShipped.SetXp(TestSkills.Chopping, atTen);
        onSteeper.SetXp(TestSkills.Chopping, atTen);

        Assert.Same(shipped, onShipped.Curve);
        Assert.Same(steeper, onSteeper.Curve);
        Assert.Equal(10, onShipped.Level(TestSkills.Chopping));
        Assert.True(onSteeper.Level(TestSkills.Chopping) < onShipped.Level(TestSkills.Chopping),
            "a steeper curve has to read the same experience as a lower level");
    }

    [Fact]
    public void Every_door_refuses_an_index_outside_the_roster()
    {
        SkillBook book = TestSkills.Fresh();
        Assert.Equal(TestSkills.Count, book.Count);
        Assert.Same(TestSkills.Roster, book.Roster);

        // A caller bug rather than bad data, so it throws rather than answering a default. The decoder
        // skips an unknown id long before a door here sees it.
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = book.Xp(TestSkills.Count));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = book.Level(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = book.AddXp(TestSkills.Count, 5d));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = book.RemoveXp(TestSkills.Count, 5d));
        Assert.Throws<ArgumentOutOfRangeException>(() => book.SetXp(-1, 5d));
    }

    [Fact]
    public void A_book_needs_both_a_roster_and_a_curve()
    {
        Assert.Throws<ArgumentNullException>(() => new SkillBook(null!, TestSkills.Curve));
        Assert.Throws<ArgumentNullException>(() => new SkillBook(TestSkills.Roster, null!));
    }
}
