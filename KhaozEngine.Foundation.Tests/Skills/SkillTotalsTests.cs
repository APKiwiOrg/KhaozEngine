using System;
using KhaozEngine.Skills;
using Xunit;

namespace KhaozEngine.Tests.Foundation.Skills;

/// <summary>The total level: every open leaf's level added up, never a parent and never a locked
/// skill.</summary>
public class SkillTotalsTests
{
    static SkillXpCurve Curve => TestSkills.Curve;

    [Fact]
    public void Only_open_leaves_count()
    {
        // The open leaves are Vitality, Striking, Chopping and Digging. Combat is an open parent over an open
        // child and a locked one, and it is the case a parent-counting total gets wrong first.
        SkillBook book = TestSkills.Fresh();
        Assert.Equal(TestSkills.SeededLevel + 3, SkillTotals.TotalLevel(book, TestSkills.Roster, Curve));

        book.SetXp(TestSkills.Chopping, Curve.XpForLevel(20));
        Assert.Equal(TestSkills.SeededLevel + 2 + 20, SkillTotals.TotalLevel(book, TestSkills.Roster, Curve));

        // A parent holding experience counts nothing, open or locked: its level is fed by the children.
        book.SetXp(TestSkills.Combat, Curve.XpForLevel(30));
        book.SetXp(TestSkills.Artisan, Curve.XpForLevel(35));
        Assert.Equal(TestSkills.SeededLevel + 2 + 20, SkillTotals.TotalLevel(book, TestSkills.Roster, Curve));

        // A locked child with experience written straight in (the decoder's path) still counts nothing.
        book.SetXp(TestSkills.Guarding, Curve.XpForLevel(40));
        book.SetXp(TestSkills.Weaving, Curve.XpForLevel(45));
        Assert.Equal(32, SkillTotals.TotalLevel(book, TestSkills.Roster, Curve));
    }

    [Fact]
    public void An_empty_book_is_one_per_open_leaf()
    {
        Assert.Equal(4, SkillTotals.TotalLevel(TestSkills.Empty(), TestSkills.Roster, Curve));

        SkillRoster closed = SkillRoster.Of(3).LockAll().Build();
        Assert.Equal(0, SkillTotals.TotalLevel(new SkillBook(closed, Curve), closed, Curve));
    }

    [Fact]
    public void The_upstream_panel_numbers_hold_on_its_own_roster()
    {
        // Grimhollow's tree at 4e2baa94, index for index, and its skills panel test's own steps and numbers:
        // Hitpoints seeded at ten plus six open leaves at one, then Woodcutting to twenty, then a parent and a
        // locked craft written straight in and counting nothing.
        SkillBook book = SkillBook.Fresh(Upstream.Roster, Curve, new SkillSeed(Upstream.Hitpoints, 10));
        Assert.Equal(16, SkillTotals.TotalLevel(book, Upstream.Roster, Curve));

        book.SetXp(Upstream.Woodcutting, Curve.XpForLevel(20));
        Assert.Equal(35, SkillTotals.TotalLevel(book, Upstream.Roster, Curve));

        book.SetXp(Upstream.Gathering, Curve.XpForLevel(30));
        Assert.Equal(35, SkillTotals.TotalLevel(book, Upstream.Roster, Curve));

        book.SetXp(Upstream.Alchemy, Curve.XpForLevel(40));
        Assert.Equal(35, SkillTotals.TotalLevel(book, Upstream.Roster, Curve));
    }

    [Fact]
    public void The_roster_and_the_curve_are_the_ones_passed_in()
    {
        SkillBook book = TestSkills.Fresh();
        book.SetXp(TestSkills.Guarding, Curve.XpForLevel(40));

        // A newer roster that opens Guarding counts it, where the one the book was built over does not.
        SkillRoster opened = SkillRoster.Of(TestSkills.Count)
            .LockAll()
            .Unlock(TestSkills.Vitality).Unlock(TestSkills.Striking).Unlock(TestSkills.Guarding)
            .Unlock(TestSkills.Chopping).Unlock(TestSkills.Digging)
            .Parent(TestSkills.Striking, TestSkills.Combat)
            .Parent(TestSkills.Guarding, TestSkills.Combat)
            .Parent(TestSkills.Chopping, TestSkills.Gathering)
            .Parent(TestSkills.Digging, TestSkills.Gathering)
            .Parent(TestSkills.Weaving, TestSkills.Artisan)
            .Build();
        Assert.Equal(TestSkills.SeededLevel + 3 + 40, SkillTotals.TotalLevel(book, opened, Curve));

        // The same experience read under the classic table is that table's levels: Vitality's 1,702 is level
        // ten on the test curve and level twelve on the classic one.
        Assert.Equal(12, SkillXpCurve.Osrs.LevelFor(book.Xp(TestSkills.Vitality)));
        Assert.Equal(12 + 3, SkillTotals.TotalLevel(book, TestSkills.Roster, SkillXpCurve.Osrs));
    }

    [Fact]
    public void A_mismatched_roster_or_a_null_argument_is_a_caller_bug()
    {
        SkillBook book = TestSkills.Fresh();
        SkillRoster shorter = SkillRoster.Flat(TestSkills.Count - 1);

        Assert.Equal("roster",
            Assert.Throws<ArgumentException>(() => SkillTotals.TotalLevel(book, shorter, Curve)).ParamName);
        Assert.Throws<ArgumentNullException>(() => SkillTotals.TotalLevel(null!, TestSkills.Roster, Curve));
        Assert.Throws<ArgumentNullException>(() => SkillTotals.TotalLevel(book, null!, Curve));
        Assert.Throws<ArgumentNullException>(() => SkillTotals.TotalLevel(book, TestSkills.Roster, null!));
    }

    /// <summary>Grimhollow's roster at 4e2baa94, with its persisted ids and its lock rule: seven live skills,
    /// and a parent locked exactly when every child under it is.</summary>
    static class Upstream
    {
        public const int Hitpoints = 0;
        public const int Fighter = 1;
        public const int Masonry = 2;
        public const int Defence = 3;
        public const int Woodcutting = 4;
        public const int Forgecraft = 5;
        public const int Carpentry = 6;
        public const int Magic = 7;
        public const int Artifice = 8;
        public const int Foraging = 9;
        public const int Alchemy = 10;
        public const int Mining = 11;
        public const int Combat = 12;
        public const int Gathering = 13;
        public const int Processing = 14;
        public const int Artisan = 15;
        public const int Fishing = 16;
        public const int Butchering = 17;
        public const int Sawmilling = 18;
        public const int Extraction = 19;
        public const int Filleting = 20;
        public const int Tanning = 21;
        public const int Herbalism = 22;

        public static SkillRoster Roster { get; } = SkillRoster.Of(23)
            .LockAll()
            .Unlock(Hitpoints).Unlock(Fighter).Unlock(Woodcutting).Unlock(Mining)
            .Unlock(Butchering).Unlock(Sawmilling).Unlock(Extraction)
            .Unlock(Combat).Unlock(Gathering).Unlock(Processing)
            .Parent(Fighter, Combat).Parent(Defence, Combat).Parent(Magic, Combat)
            .Parent(Woodcutting, Gathering).Parent(Mining, Gathering).Parent(Fishing, Gathering)
            .Parent(Butchering, Gathering).Parent(Foraging, Gathering)
            .Parent(Sawmilling, Processing).Parent(Extraction, Processing).Parent(Filleting, Processing)
            .Parent(Tanning, Processing).Parent(Herbalism, Processing)
            .Parent(Carpentry, Artisan).Parent(Masonry, Artisan).Parent(Forgecraft, Artisan)
            .Parent(Artifice, Artisan).Parent(Alchemy, Artisan)
            .Build();
    }
}
