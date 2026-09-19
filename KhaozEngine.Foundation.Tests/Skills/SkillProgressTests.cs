using System;
using KhaozEngine.Skills;
using Xunit;

namespace KhaozEngine.Tests.Foundation.Skills;

/// <summary>The readout arithmetic: where a number sits between two levels, what the next one costs, and
/// what is still owed. Pure and clamped at both ends, so a corrupted number reads as an empty bar rather
/// than as a bar of undefined width.</summary>
public class SkillProgressTests
{
    static SkillXpCurve Curve => TestSkills.Curve;

    [Fact]
    public void The_fraction_runs_from_zero_at_a_threshold_to_one_at_the_next()
    {
        double floor = Curve.XpForLevel(10);       // 1702
        double ceiling = Curve.XpForLevel(11);     // 2024

        Assert.Equal(0d, SkillProgress.FractionToNext(Curve, floor));
        Assert.Equal(0.5d, SkillProgress.FractionToNext(Curve, floor + ((ceiling - floor) / 2d)), 9);
        Assert.Equal(1d, SkillProgress.FractionToNext(Curve, Math.BitDecrement(ceiling)), 6);
        // The next threshold is the NEXT level's zero rather than this one's one.
        Assert.Equal(0d, SkillProgress.FractionToNext(Curve, ceiling));
    }

    [Fact]
    public void A_corrupted_or_empty_number_reads_as_an_empty_bar()
    {
        Assert.Equal(0d, SkillProgress.FractionToNext(Curve, 0d));
        Assert.Equal(0d, SkillProgress.FractionToNext(Curve, -5d));
        Assert.Equal(0d, SkillProgress.FractionToNext(Curve, double.NaN));
        Assert.Equal(0d, SkillProgress.FractionToNext(Curve, double.PositiveInfinity));
    }

    [Fact]
    public void The_top_of_the_table_is_a_solid_bar_with_nothing_owed()
    {
        double maxed = Curve.XpForLevel(Curve.MaxLevel);

        Assert.True(SkillProgress.IsMaxed(Curve, maxed));
        Assert.Equal(1d, SkillProgress.FractionToNext(Curve, maxed));
        Assert.Equal(0d, SkillProgress.RemainingToNext(Curve, maxed));
        Assert.Equal(0d, SkillProgress.RemainingToNextWhole(Curve, maxed));
        // There is no next level to quote, so it answers the top level's own threshold, which is what the
        // readout says when there is nothing left to earn.
        Assert.Equal(maxed, SkillProgress.XpForNext(Curve, maxed));

        Assert.False(SkillProgress.IsMaxed(Curve, Math.BitDecrement(maxed)));
        Assert.False(SkillProgress.IsMaxed(Curve, 0d));
        Assert.False(SkillProgress.IsMaxed(Curve, double.NaN));
    }

    [Fact]
    public void XpForNext_is_the_next_threshold_and_remaining_is_the_gap_to_it()
    {
        double at = Curve.XpForLevel(10) + 50d;

        Assert.Equal(Curve.XpForLevel(11), SkillProgress.XpForNext(Curve, at));
        Assert.Equal(Curve.XpForLevel(11) - at, SkillProgress.RemainingToNext(Curve, at));
        // A fresh, untrained skill owes the whole of level two.
        Assert.Equal(Curve.XpForLevel(2), SkillProgress.XpForNext(Curve, 0d));
        Assert.Equal(Curve.XpForLevel(2), SkillProgress.RemainingToNext(Curve, 0d));
        // A corrupted number is treated as nothing held rather than propagated into the readout.
        Assert.Equal(Curve.XpForLevel(2), SkillProgress.RemainingToNext(Curve, double.NaN));
    }

    [Fact]
    public void The_whole_remainder_is_quoted_against_the_floored_number_a_panel_prints()
    {
        // Experience is fractional wherever a game pays a fraction of a point, so the exact remainder can
        // be a third of a point while the line above it prints a floored number. Printed floored that
        // reads as nothing owed under a bar visibly short of full. Quoted against the floor the three
        // lines add up: shown plus owed is the next threshold.
        double held = Curve.XpForLevel(10) + 0.4d;

        Assert.Equal(Curve.XpForLevel(11) - held, SkillProgress.RemainingToNext(Curve, held), 9);
        Assert.Equal(Curve.XpForLevel(11) - Math.Floor(held), SkillProgress.RemainingToNextWhole(Curve, held));
        Assert.Equal(Curve.XpForLevel(11),
            Math.Floor(held) + SkillProgress.RemainingToNextWhole(Curve, held));
        Assert.True(SkillProgress.RemainingToNextWhole(Curve, held) > SkillProgress.RemainingToNext(Curve, held));
    }

    [Fact]
    public void Nothing_owed_is_ever_negative()
    {
        // Every path floors at zero, so a number sitting exactly on a threshold or past the top of the
        // table can never answer a negative amount owed.
        for (int level = 1; level <= Curve.MaxLevel; level++)
        {
            Assert.True(SkillProgress.RemainingToNext(Curve, Curve.XpForLevel(level)) >= 0d);
            Assert.True(SkillProgress.RemainingToNextWhole(Curve, Curve.XpForLevel(level)) >= 0d);
        }
        Assert.Equal(0d, SkillProgress.RemainingToNext(Curve, SkillXpLimits.MaxXp));
    }

    [Fact]
    public void The_curve_arrives_by_argument_and_is_required()
    {
        // Off a book's own curve, which is the point: a panel and an award read the same table rather than
        // two answers from whatever was published last.
        SkillBook book = TestSkills.Fresh();
        Assert.Equal(0d, SkillProgress.FractionToNext(book.Curve, book.Xp(TestSkills.Vitality)));

        Assert.Throws<ArgumentNullException>(() => _ = SkillProgress.FractionToNext(null!, 5d));
        Assert.Throws<ArgumentNullException>(() => _ = SkillProgress.XpForNext(null!, 5d));
        Assert.Throws<ArgumentNullException>(() => _ = SkillProgress.RemainingToNext(null!, 5d));
        Assert.Throws<ArgumentNullException>(() => _ = SkillProgress.RemainingToNextWhole(null!, 5d));
        Assert.Throws<ArgumentNullException>(() => _ = SkillProgress.IsMaxed(null!, 5d));
    }
}
