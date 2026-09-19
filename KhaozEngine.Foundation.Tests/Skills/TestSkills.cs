using KhaozEngine.Skills;

namespace KhaozEngine.Tests.Foundation.Skills;

/// <summary>The roster and the curve every skills test runs on, standing in for a game's own.</summary>
/// <remarks>
/// Shaped like a real one rather than like a convenience: a root leaf that a fresh character is seeded in,
/// two live parents with live and locked children under them, and a whole branch that is locked top to
/// bottom. That is what lets one roster carry the lock rule, the share rule, the root rule and the
/// codec's skip rule without a second fixture.
/// <para>The curve is a first level costing 114 doubling every six to a cap of 100, so the landmark numbers
/// the cases assert are hand-checkable: level 2 is 114, level 10 is 1702, level 11 is 2024 and level 100 is
/// 86,276,710.</para>
/// </remarks>
public static class TestSkills
{
    /// <summary>A root leaf with no children, open, and the one a fresh book seeds.</summary>
    public const int Vitality = 0;

    /// <summary>A root with live children.</summary>
    public const int Combat = 1;

    /// <summary>A root with live children.</summary>
    public const int Gathering = 2;

    /// <summary>A root whose every child is locked, and which is locked itself.</summary>
    public const int Artisan = 3;

    /// <summary>A live child of <see cref="Combat"/>.</summary>
    public const int Striking = 4;

    /// <summary>A locked child of <see cref="Combat"/>.</summary>
    public const int Guarding = 5;

    /// <summary>A live child of <see cref="Gathering"/>.</summary>
    public const int Chopping = 6;

    /// <summary>A live child of <see cref="Gathering"/>.</summary>
    public const int Digging = 7;

    /// <summary>A locked child of <see cref="Artisan"/>.</summary>
    public const int Weaving = 8;

    /// <summary>How many skills the test roster holds.</summary>
    public const int Count = 9;

    /// <summary>The level a fresh test character's <see cref="Vitality"/> starts at.</summary>
    public const int SeededLevel = 10;

    /// <summary>The roster under test.</summary>
    public static SkillRoster Roster { get; } = SkillRoster.Of(Count)
        .LockAll()
        .Unlock(Vitality).Unlock(Combat).Unlock(Gathering)
        .Unlock(Striking).Unlock(Chopping).Unlock(Digging)
        .Parent(Striking, Combat)
        .Parent(Guarding, Combat)
        .Parent(Chopping, Gathering)
        .Parent(Digging, Gathering)
        .Parent(Weaving, Artisan)
        .Build();

    /// <summary>The curve under test.</summary>
    public static SkillXpCurve Curve { get; } = SkillXpCurve.Configured(114, 6, 100);

    /// <summary>A fresh book on the roster and curve above, seeded the way a game would seed one.</summary>
    public static SkillBook Fresh() => SkillBook.Fresh(Roster, Curve, new SkillSeed(Vitality, SeededLevel));

    /// <summary>An empty book, for the cases that want nothing seeded.</summary>
    public static SkillBook Empty() => new(Roster, Curve);
}
