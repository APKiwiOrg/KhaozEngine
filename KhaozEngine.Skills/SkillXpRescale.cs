using System;

namespace KhaozEngine.Skills;

/// <summary>
/// Carries a saved character from the experience curve they earned under to the one in force, keeping the
/// LEVEL and the fraction of the way to the next one.
/// </summary>
/// <remarks>
/// A game tunes its curve, so this is not a one-off migration for one edit. Experience is stored as a
/// NUMBER, and a number means a different level under a different curve: the first tuning pass on a real
/// game landed a fresh character's starting experience three levels lower, which is a character logging in
/// weaker having lost nothing they can see. So a record carries the curve it was written under and every
/// load that finds a different one runs through here.
/// <para>The rule is a deliberate default: a curve change never moves anybody's level. It is not "keep the
/// experience number", which reads as a demotion, and not "keep the level and drop the progress", which
/// quietly steals up to a level's worth of grind. The fraction rides across too, so a character three
/// quarters of the way to 43 is still three quarters of the way to 43.</para>
/// <para>Pure arithmetic over two curves, so a test asserts the whole matrix without a record, a store or
/// a server. <see cref="Rebase"/> is the one place that walks a book.</para>
/// </remarks>
public static class SkillXpRescale
{
    /// <summary>The experience that stands for the same progress under a different curve.</summary>
    /// <param name="from">The curve the number was earned under.</param>
    /// <param name="to">The curve in force now.</param>
    /// <param name="xp">The stored experience.</param>
    /// <returns>Experience under <paramref name="to"/> at the same level and the same fraction toward the
    /// next one. Zero stays zero, and a maxed skill stays maxed.</returns>
    /// <exception cref="ArgumentNullException">Either curve is null.</exception>
    public static double Preserve(SkillXpCurve from, SkillXpCurve to, double xp)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        // Nothing earned is nothing earned under any curve, and a stored NaN is not a level to preserve.
        if (!double.IsFinite(xp) || xp <= 0d) return 0d;

        int level = from.LevelFor(xp);
        // The top of either table has no next level to be a fraction of the way to. A character at the old
        // cap arrives at the new one, and a character above the new cap is pinned to it rather than losing
        // the levels between, which is the only direction that does not take something away.
        if (level >= from.MaxLevel || level >= to.MaxLevel) return to.XpForLevel(to.MaxLevel);

        double floor = from.XpForLevel(level);
        double ceiling = from.XpForLevel(level + 1);
        double span = ceiling - floor;
        double fraction = span > 0d ? Math.Clamp((xp - floor) / span, 0d, 1d) : 0d;

        double toFloor = to.XpForLevel(level);
        double toCeiling = to.XpForLevel(level + 1);
        double rescaled = toFloor + (fraction * (toCeiling - toFloor));
        // A fraction of a whisker under one, times a span in the tens of millions, can round up onto the
        // next threshold and hand out a free level. Held below it, because the contract this function is
        // asked for is the LEVEL first and the fraction second.
        return rescaled >= toCeiling ? Math.BitDecrement(toCeiling) : rescaled;
    }

    /// <summary>Rewrites every skill in a book onto a different curve, in place.</summary>
    /// <param name="book">The decoded book.</param>
    /// <param name="from">The curve the book was saved under.</param>
    /// <param name="to">The curve in force now.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static void Rebase(SkillBook book, SkillXpCurve from, SkillXpCurve to)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        for (int skill = 0; skill < book.Count; skill++)
        {
            double xp = book.Xp(skill);
            // A skill nobody has trained is left exactly alone rather than written back as a computed zero,
            // so a rebase over an untouched book is a no-op down to the stored bytes. A LOCKED skill is
            // rebased like any other, because a lock stops awards and this is not one: a decoded book can
            // hold experience in a skill the current roster has closed, and leaving that number under the
            // old curve would misprice it the moment the roster opens again.
            if (xp <= 0d) continue;
            book.SetXp(skill, Preserve(from, to, xp));
        }
    }
}
