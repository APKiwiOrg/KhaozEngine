using System;

namespace KhaozEngine.Skills;

/// <summary>
/// The one way experience is awarded to a skill that has a parent. The child is paid in full and its parent
/// the caller's share of the same amount, and the result says what both totals became so they can be
/// recorded together. A root pays nothing upward and a locked parent is never paid.
/// </summary>
/// <remarks>
/// Every award to a CHILD skill goes through <see cref="Apply"/>. A root may be paid directly through
/// <see cref="SkillBook.AddXp"/>, since it owes no parent a share, and <see cref="Apply"/> answers the same
/// thing for it anyway.
/// </remarks>
public static class SkillAwards
{
    /// <summary>The denominator the parent's share is quoted against, so a share is basis points: 10,000 is
    /// the whole award again and 5,000 is half of it.</summary>
    /// <remarks>Integer basis points rather than a float fraction, because the share is tuning a game
    /// publishes and a published 0.15 is not the same number on every machine, while 1500 is.</remarks>
    public const int ShareDenominator = 10_000;

    /// <summary>The parent's cut of an award, in the same units as the award.</summary>
    /// <param name="amount">The experience the child was paid.</param>
    /// <param name="shareBp">The parent's share, in basis points of
    /// <see cref="ShareDenominator"/>.</param>
    public static double ParentShare(double amount, int shareBp) => amount * shareBp / ShareDenominator;

    /// <summary>Applies an award. The share arrives by ARGUMENT, off whatever content priced the award,
    /// and there is no overload that reaches for a config in force.</summary>
    /// <remarks>A locked child pays NOBODY, the same rule <see cref="SkillBook.AddXp"/> carries: with the
    /// parent's share read off the child's award, a child the book refuses cannot be left funding a live
    /// parent out of experience it never earned. The refusal is silent, so a caller that mirrors awards
    /// into its own log asks its roster whether the skill is locked rather than looking for a second
    /// failure shape here.</remarks>
    /// <param name="book">The book being paid.</param>
    /// <param name="skill">The child being paid.</param>
    /// <param name="amount">The experience to add.</param>
    /// <param name="shareBp">The parent's share, in basis points of
    /// <see cref="ShareDenominator"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="book"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="skill"/> is outside the
    /// roster.</exception>
    public static SkillAwardResult Apply(SkillBook book, int skill, double amount, int shareBp)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (skill < 0 || skill >= book.Count)
            throw new ArgumentOutOfRangeException(nameof(skill), skill,
                $"the roster holds {book.Count} skills, so an index is 0 to {book.Count - 1}");
        ISkillRoster roster = book.Roster;
        if (roster.IsLocked(skill)) return new SkillAwardResult(false, -1, book.Xp(skill), false, 0d);
        bool crossed = book.AddXp(skill, amount);
        double childXp = book.Xp(skill);
        // A roster usually locks a parent only when every child is locked, so a live child cannot reach a
        // locked parent. The parent guard stays as a defensive check for the rosters that do not.
        int parent = roster.ParentOf(skill);
        if (parent < 0 || parent >= book.Count || roster.IsLocked(parent))
            return new SkillAwardResult(crossed, -1, childXp, false, 0d);
        double share = ParentShare(amount, shareBp);
        if (share <= 0d) return new SkillAwardResult(crossed, parent, childXp, false, book.Xp(parent));
        bool parentCrossed = book.AddXp(parent, share);
        return new SkillAwardResult(crossed, parent, childXp, parentCrossed, book.Xp(parent));
    }
}
