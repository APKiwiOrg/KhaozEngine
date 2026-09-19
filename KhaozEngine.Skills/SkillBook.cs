using System;

namespace KhaozEngine.Skills;

/// <summary>One character's experience, per skill, and the levels it buys.</summary>
/// <remarks>
/// Sized by the ROSTER and priced by the CURVE, both handed in at construction. Neither is ambient: a book
/// that read either off a static holder would answer a different level the moment a publish moved the
/// knobs, with nothing holding it saying so.
/// </remarks>
public sealed class SkillBook
{
    readonly double[] _xp;

    /// <summary>Builds an empty book over one roster and one curve.</summary>
    /// <param name="roster">Which skills exist, how they nest, and which are open.</param>
    /// <param name="curve">The curve this book's numbers MEAN, which is what a level query reads and what
    /// a record written from it stores.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public SkillBook(ISkillRoster roster, SkillXpCurve curve)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roster.Count, "roster.Count");
        Roster = roster;
        Curve = curve;
        _xp = new double[roster.Count];
    }

    /// <summary>The roster this book is sized by and reads its lock and parent rules off.</summary>
    public ISkillRoster Roster { get; }

    /// <summary>The curve this book's experience is read against.</summary>
    /// <remarks>A decoded book carries the curve it was decoded under, which is the CURRENT one: a caller
    /// rescales a record written under a different curve as it loads it, so what a book holds is always
    /// experience under this table.</remarks>
    public SkillXpCurve Curve { get; }

    /// <summary>How many skills this book holds, which is its roster's count.</summary>
    public int Count => _xp.Length;

    /// <summary>A brand new character: every skill at level one and zero experience, except the ones the
    /// game seeds.</summary>
    /// <remarks>A seed is written STRAIGHT IN, past the lock rule, because a starting level on a skill no
    /// system has opened yet is a legitimate design statement and the lock is about awards.</remarks>
    /// <param name="roster">The roster the book is sized by.</param>
    /// <param name="curve">The curve the fresh numbers are quoted under.</param>
    /// <param name="seeds">The skills that do not start at level one.</param>
    /// <exception cref="ArgumentOutOfRangeException">A seed names a skill outside the roster.</exception>
    public static SkillBook Fresh(ISkillRoster roster, SkillXpCurve curve, params ReadOnlySpan<SkillSeed> seeds)
    {
        var book = new SkillBook(roster, curve);
        foreach (SkillSeed seed in seeds)
        {
            if (seed.Skill < 0 || seed.Skill >= book.Count)
                throw new ArgumentOutOfRangeException(nameof(seeds), seed.Skill,
                    $"the roster holds {book.Count} skills, so a seed index is 0 to {book.Count - 1}");
            book._xp[seed.Skill] = curve.XpForLevel(seed.Level);
        }
        return book;
    }

    /// <summary>Experience held in one skill.</summary>
    /// <param name="skill">The skill index.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="skill"/> is outside the roster.</exception>
    public double Xp(int skill) => _xp[Checked(skill)];

    /// <summary>The level one skill's experience buys, off the book's OWN curve.</summary>
    /// <param name="skill">The skill index.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="skill"/> is outside the roster.</exception>
    public int Level(int skill) => Curve.LevelFor(_xp[Checked(skill)]);

    /// <summary>Awards experience. Returns true when the award CROSSED a level, which is the caller's cue
    /// to present a level-up. A non-finite or non-positive amount is dropped and reports false.</summary>
    /// <remarks>A LOCKED skill is refused here, adding nothing and reporting false, rather than being left
    /// to every award site to remember. A roster is usually the shape of a finished game and most of it
    /// has no system behind it, so the one place that can store experience is the one place worth
    /// guarding.</remarks>
    /// <param name="skill">The skill being paid.</param>
    /// <param name="amount">The experience to add.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="skill"/> is outside the roster.</exception>
    public bool AddXp(int skill, double amount)
    {
        int at = Checked(skill);
        if (!double.IsFinite(amount) || amount <= 0d) return false;
        if (Roster.IsLocked(at)) return false;
        int before = Level(at);
        _xp[at] = Math.Min(SkillXpLimits.MaxXp, _xp[at] + amount);
        return Level(at) > before;
    }

    /// <summary>Takes experience away. Returns true when the removal DROPPED a level. A non-finite or
    /// non-positive amount is dropped and reports false.</summary>
    /// <remarks>The mirror of <see cref="AddXp"/> and guarded the same way: a LOCKED skill is refused,
    /// taking nothing and reporting false. The book floors at zero rather than storing a negative, because
    /// a negative total has no level and the codec refuses one.</remarks>
    /// <param name="skill">The skill being taken from.</param>
    /// <param name="amount">The experience to remove.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="skill"/> is outside the roster.</exception>
    public bool RemoveXp(int skill, double amount)
    {
        int at = Checked(skill);
        if (!double.IsFinite(amount) || amount <= 0d) return false;
        if (Roster.IsLocked(at)) return false;
        int before = Level(at);
        _xp[at] = Math.Max(0d, _xp[at] - amount);
        return Level(at) < before;
    }

    /// <summary>Writes one skill's experience outright, for a decoder and for a rescale and for nothing
    /// else. Non-finite or negative is refused rather than stored, because a stored NaN makes every later
    /// level query answer one.</summary>
    /// <param name="skill">The skill being written.</param>
    /// <param name="xp">The experience to store, saturating at <see cref="SkillXpLimits.MaxXp"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="skill"/> is outside the roster.</exception>
    public void SetXp(int skill, double xp)
    {
        int at = Checked(skill);
        if (!double.IsFinite(xp) || xp < 0d) return;
        _xp[at] = Math.Min(SkillXpLimits.MaxXp, xp);
    }

    // An index outside the roster is a CALLER bug rather than bad data: the decoder skips an id it does not
    // have before it ever reaches a door here, so anything that arrives out of range came from code.
    int Checked(int skill)
    {
        if (skill < 0 || skill >= _xp.Length)
            throw new ArgumentOutOfRangeException(nameof(skill), skill,
                $"the roster holds {_xp.Length} skills, so an index is 0 to {_xp.Length - 1}");
        return skill;
    }
}
