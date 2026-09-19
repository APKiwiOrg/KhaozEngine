using System;
using KhaozEngine.Skills;
using Xunit;

namespace KhaozEngine.Tests.Foundation.Skills;

/// <summary>
/// The experience curve: the cost of a level doubles every <c>doublingLevels</c> levels from a first level
/// costing <c>firstLevelCost</c>, and a threshold is the running sum under it.
/// <para>Pinned two ways on purpose. The landmarks are LITERALS, hand-checkable (114 for level 2,
/// 86,276,710 for level 100), so a rewrite of the recurrence that agreed with itself still fails. The sweep
/// beside them re-derives the formula independently, which is what catches a threshold that is right at the
/// two ends and wrong in the middle.</para>
/// </summary>
public class SkillXpCurveTests
{
    [Fact]
    public void The_curve_hits_its_own_numbers()
    {
        Assert.Equal(0d, TestSkills.Curve.XpForLevel(1));
        Assert.Equal(114d, TestSkills.Curve.XpForLevel(2));
        Assert.Equal(86_276_710d, TestSkills.Curve.XpForLevel(100));

        Assert.Equal(100, TestSkills.Curve.MaxLevel);
        Assert.Equal(114, TestSkills.Curve.FirstLevelCost);
        Assert.Equal(6, TestSkills.Curve.DoublingLevels);
    }

    [Fact]
    public void Every_threshold_is_the_running_sum_of_the_doubling_cost()
    {
        // The formula, re-derived here rather than read off the table: cost(L) = firstLevelCost *
        // 2^((L-1)/D), rounded, and the threshold for L+1 is the running sum. Written out because a test
        // that called the production helper would agree with any bug in it.
        SkillXpCurve curve = TestSkills.Curve;
        double running = 0d;
        Assert.Equal(0d, curve.XpForLevel(1));
        for (int level = 1; level < curve.MaxLevel; level++)
        {
            running += Math.Round(curve.FirstLevelCost * Math.Pow(2d, (level - 1) / (double)curve.DoublingLevels),
                MidpointRounding.AwayFromZero);
            Assert.Equal(running, curve.XpForLevel(level + 1));
        }

        // The cost DOUBLES over the configured span, which is the one thing a running sum could satisfy
        // while getting the exponent wrong. Ranged rather than exact because each cost is rounded whole,
        // which is worth about half a point either way and matters most where the costs are smallest.
        for (int level = 1; level + curve.DoublingLevels < curve.MaxLevel; level++)
        {
            double here = curve.XpForLevel(level + 1) - curve.XpForLevel(level);
            int further = level + curve.DoublingLevels;
            double later = curve.XpForLevel(further + 1) - curve.XpForLevel(further);
            Assert.InRange(later / here, 1.99d, 2.01d);
        }
    }

    [Fact]
    public void LevelFor_is_the_inverse_and_clamps_at_both_ends()
    {
        SkillXpCurve curve = TestSkills.Curve;

        Assert.Equal(1, curve.LevelFor(-5d));
        Assert.Equal(1, curve.LevelFor(0d));
        Assert.Equal(1, curve.LevelFor(113d));
        Assert.Equal(2, curve.LevelFor(114d));
        Assert.Equal(1, curve.LevelFor(double.NaN));

        // Every level's own threshold buys exactly that level, and one point short buys the one below.
        for (int level = 2; level <= curve.MaxLevel; level++)
        {
            double at = curve.XpForLevel(level);
            Assert.Equal(level, curve.LevelFor(at));
            Assert.Equal(level - 1, curve.LevelFor(at - 1d));
        }

        // Past the top of the table, including the ceiling a saved character saturates at.
        Assert.Equal(curve.MaxLevel, curve.LevelFor(SkillXpLimits.MaxXp));
        // And XpForLevel clamps rather than throwing on a level off either end.
        Assert.Equal(0d, curve.XpForLevel(-3));
        Assert.Equal(curve.XpForLevel(curve.MaxLevel), curve.XpForLevel(curve.MaxLevel + 50));
    }

    [Fact]
    public void The_top_of_the_table_fits_under_the_ceiling_a_saved_character_can_hold()
    {
        // The rule a content load should enforce: a curve whose top level cost more than the codec's
        // ceiling would decode a maxed skill as something else.
        Assert.True(TestSkills.Curve.XpForLevel(TestSkills.Curve.MaxLevel) <= SkillXpLimits.MaxXp);
        Assert.Equal(200_000_000d, SkillXpLimits.MaxXp);
    }

    [Fact]
    public void The_hash_is_the_identity_and_the_classic_curve_is_nameable()
    {
        SkillXpCurve curve = TestSkills.Curve;

        // Same parameters, same identity, which is what a load compares before deciding to rescale.
        Assert.Equal(curve.Hash,
            SkillXpCurve.Configured(curve.FirstLevelCost, curve.DoublingLevels, curve.MaxLevel).Hash);
        Assert.NotEqual(curve.Hash, SkillXpCurve.Configured(115, 6, 100).Hash);
        Assert.True(curve.IsParametric);

        // The classic curve is a WORD rather than a digest, because it names a shape rather than a set of
        // parameters, and because a record with no curve section at all is read as this one.
        Assert.Equal("osrs", SkillXpCurve.Osrs.Hash);
        Assert.Equal(SkillXpCurve.OsrsHash, SkillXpCurve.Osrs.Hash);
        Assert.False(SkillXpCurve.Osrs.IsParametric);
        Assert.Equal(99, SkillXpCurve.Osrs.MaxLevel);
        Assert.Same(SkillXpCurve.Osrs, SkillXpCurve.Osrs);
        // Its own landmarks, so the recurrence is pinned rather than merely present.
        Assert.Equal(83d, SkillXpCurve.Osrs.XpForLevel(2));
        Assert.Equal(1154d, SkillXpCurve.Osrs.XpForLevel(10));
        Assert.Equal(13_034_431d, SkillXpCurve.Osrs.XpForLevel(99));
    }

    [Fact]
    public void Configured_refuses_a_curve_that_is_not_a_curve()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SkillXpCurve.Configured(0, 6, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => SkillXpCurve.Configured(114, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => SkillXpCurve.Configured(114, -1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => SkillXpCurve.Configured(114, 6, 1));
    }
}
