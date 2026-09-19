using System;

namespace KhaozEngine.Skills;

/// <summary>Where a skill's experience sits BETWEEN two levels, which is the one question the curve cannot
/// answer on its own: <see cref="SkillXpCurve"/> says what a level costs and what a number buys, and
/// neither of those is the fraction a progress bar fills to.</summary>
/// <remarks>Shared rather than client-side because it is arithmetic on the shared curve and nothing else:
/// a server awards against the same table, so a later server-side readout reads the same number. Pure,
/// total, and clamped at both ends, so a stored NaN or a negative reads as an empty bar rather than as a
/// bar of undefined width.
/// <para>The curve arrives BY ARGUMENT, off a book's own <see cref="SkillBook.Curve"/> or off whatever the
/// caller holds. Taken off an ambient holder it would make a panel and an award two answers the moment a
/// publish moved the knobs.</para></remarks>
public static class SkillProgress
{
    /// <summary>How far through the current level an amount of experience is, from zero at the level's own
    /// threshold to one at the next level's. Exactly one at the curve's top level, where there is no next
    /// level to be short of.</summary>
    /// <param name="curve">The curve the experience is quoted under.</param>
    /// <param name="xp">The skill's experience.</param>
    /// <exception cref="ArgumentNullException"><paramref name="curve"/> is null.</exception>
    public static double FractionToNext(SkillXpCurve curve, double xp)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (!double.IsFinite(xp) || xp <= 0d) return 0d;
        int level = curve.LevelFor(xp);
        if (level >= curve.MaxLevel) return 1d;
        double from = curve.XpForLevel(level);
        double span = curve.XpForLevel(level + 1) - from;
        if (span <= 0d) return 1d;
        return Math.Clamp((xp - from) / span, 0d, 1d);
    }

    /// <summary>The experience the NEXT level costs. At the top of the table there is none, so it answers
    /// the top level's own threshold, which is what the readout says when there is nothing left to
    /// earn.</summary>
    /// <param name="curve">The curve the experience is quoted under.</param>
    /// <param name="xp">The skill's experience.</param>
    /// <exception cref="ArgumentNullException"><paramref name="curve"/> is null.</exception>
    public static double XpForNext(SkillXpCurve curve, double xp)
    {
        ArgumentNullException.ThrowIfNull(curve);
        int level = IsMaxed(curve, xp) ? curve.MaxLevel : curve.LevelFor(xp) + 1;
        return curve.XpForLevel(level);
    }

    /// <summary>How much experience is still owed for the next level, never below zero and zero at the top
    /// of the table.</summary>
    /// <param name="curve">The curve the experience is quoted under.</param>
    /// <param name="xp">The skill's experience.</param>
    /// <exception cref="ArgumentNullException"><paramref name="curve"/> is null.</exception>
    public static double RemainingToNext(SkillXpCurve curve, double xp)
    {
        if (IsMaxed(curve, xp)) return 0d;
        double held = double.IsFinite(xp) && xp > 0d ? xp : 0d;
        return Math.Max(0d, XpForNext(curve, held) - held);
    }

    /// <summary>What a READOUT says is still owed: the whole experience owed against the FLOORED
    /// experience a panel prints, which is the ceiling of <see cref="RemainingToNext"/> because every
    /// threshold in the table is whole.</summary>
    /// <remarks>Experience is fractional wherever a game pays a fraction of a point per action, so the
    /// exact remainder can be a third of a point while the line under it prints a floored number. Printed
    /// floored, that reads as nothing owed under a bar visibly short of full and a next level that has not
    /// arrived. Rounded up, the three lines add up: the experience shown plus this is the next level's own
    /// threshold.</remarks>
    /// <param name="curve">The curve the experience is quoted under.</param>
    /// <param name="xp">The skill's experience.</param>
    /// <exception cref="ArgumentNullException"><paramref name="curve"/> is null.</exception>
    public static double RemainingToNextWhole(SkillXpCurve curve, double xp)
    {
        if (IsMaxed(curve, xp)) return 0d;
        double held = double.IsFinite(xp) && xp > 0d ? xp : 0d;
        return Math.Max(0d, XpForNext(curve, held) - Math.Floor(held));
    }

    /// <summary>Whether a skill has run out of table: at the curve's top level there is no next level,
    /// which is what turns the bar solid and drops the two owed-experience lines from a tooltip.</summary>
    /// <param name="curve">The curve the experience is quoted under.</param>
    /// <param name="xp">The skill's experience.</param>
    /// <exception cref="ArgumentNullException"><paramref name="curve"/> is null.</exception>
    public static bool IsMaxed(SkillXpCurve curve, double xp)
    {
        ArgumentNullException.ThrowIfNull(curve);
        return double.IsFinite(xp) && xp > 0d && curve.LevelFor(xp) >= curve.MaxLevel;
    }
}
