using System;

namespace KhaozEngine.Skills;

/// <summary>Readouts summed across a whole book rather than read off one skill. Beside
/// <see cref="SkillProgress"/> and shared for the same reason: a server rule and a client panel that read the
/// same number off the same arithmetic cannot drift apart.</summary>
public static class SkillTotals
{
    // Rosters at or under this size keep their parent flags on the stack. The book codec's count is one byte,
    // so every roster a record can carry fits, and a larger one pays one small array per call.
    const int StackFlagLimit = 256;

    /// <summary>Every open LEAF skill's level added up.</summary>
    /// <remarks>A locked skill contributes nothing, even holding experience it was decoded with, because a
    /// readout shows it no level, and a total counting it would move the day a skill opens rather than the day
    /// somebody trains it. A PARENT contributes nothing, open or locked, because its level is fed by the
    /// children already counted and adding it would count the same training twice. A parent is any skill some
    /// other skill names in <see cref="ISkillRoster.ParentOf"/>, locked children included, so locking every
    /// child does not turn a parent into a leaf.
    /// <para>The roster and the curve arrive BY ARGUMENT, as <see cref="SkillProgress"/>'s curve does: a caller
    /// that holds a newer roster or curve than the book was decoded under reads the total under the one it
    /// holds.</para></remarks>
    /// <param name="book">The experience the levels come from.</param>
    /// <param name="roster">Which skills are open and which are parents. Its count must match the book's.</param>
    /// <param name="curve">The curve the experience is read against.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="roster"/> holds a different number of skills than
    /// <paramref name="book"/>, which is a caller bug rather than a total.</exception>
    public static int TotalLevel(SkillBook book, ISkillRoster roster, SkillXpCurve curve)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(curve);
        int count = roster.Count;
        if (count != book.Count)
            throw new ArgumentException(
                $"the roster holds {count} skills and the book holds {book.Count}", nameof(roster));

        Span<bool> isParent = count <= StackFlagLimit ? stackalloc bool[StackFlagLimit] : new bool[count];
        isParent = isParent[..count];
        isParent.Clear();
        for (int child = 0; child < count; child++)
        {
            int parent = roster.ParentOf(child);
            if ((uint)parent < (uint)count) isParent[parent] = true;
        }

        int total = 0;
        for (int skill = 0; skill < count; skill++)
        {
            if (roster.IsLocked(skill) || isParent[skill]) continue;
            total += curve.LevelFor(book.Xp(skill));
        }
        return total;
    }
}
