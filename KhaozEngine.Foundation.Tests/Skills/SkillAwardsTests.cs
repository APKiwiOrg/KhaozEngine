using System;
using KhaozEngine.Skills;
using Xunit;

namespace KhaozEngine.Tests.Foundation.Skills;

/// <summary>
/// One award pays the child in full and its parent the caller's share, and the result carries both totals
/// so a caller can record them together. A root pays nothing upward and a locked child is never paid.
/// </summary>
public class SkillAwardsTests
{
    const int Half = SkillAwards.ShareDenominator / 2;

    [Fact]
    public void A_child_award_pays_the_child_in_full_and_the_parent_the_share()
    {
        SkillBook book = TestSkills.Fresh();

        SkillAwardResult award = SkillAwards.Apply(book, TestSkills.Chopping, 100d, Half);

        Assert.Equal(100d, book.Xp(TestSkills.Chopping));
        Assert.Equal(50d, book.Xp(TestSkills.Gathering));
        Assert.Equal(TestSkills.Gathering, award.Parent);
        Assert.True(award.HasParent);
        // Both totals come back off the award rather than off a second read of the book, which is what a
        // durable log storing whole totals needs.
        Assert.Equal(100d, award.ChildXp);
        Assert.Equal(50d, award.ParentXp);
    }

    [Fact]
    public void A_root_award_pays_nothing_upward()
    {
        SkillBook book = TestSkills.Fresh();
        double before = book.Xp(TestSkills.Vitality);

        SkillAwardResult award = SkillAwards.Apply(book, TestSkills.Vitality, 40d, Half);

        Assert.Equal(before + 40d, book.Xp(TestSkills.Vitality));
        Assert.Equal(-1, award.Parent);
        Assert.False(award.HasParent);
        Assert.False(award.ParentCrossed);
        Assert.Equal(0d, award.ParentXp);
        Assert.Equal(before + 40d, award.ChildXp);
    }

    [Fact]
    public void A_locked_child_pays_nobody_and_reports_the_book_as_it_stands()
    {
        SkillBook book = TestSkills.Fresh();
        book.SetXp(TestSkills.Guarding, 77d);   // a decoded book can hold experience in a locked skill

        SkillAwardResult award = SkillAwards.Apply(book, TestSkills.Guarding, 100d, Half);

        Assert.Equal(77d, book.Xp(TestSkills.Guarding));
        Assert.Equal(0d, book.Xp(TestSkills.Combat));
        Assert.False(award.Crossed);
        Assert.Equal(-1, award.Parent);
        // The result never lies about the book: a refused award reports the total as it stands rather
        // than a zero that would read as an award of nothing.
        Assert.Equal(77d, award.ChildXp);
    }

    [Fact]
    public void A_locked_parent_is_never_paid()
    {
        // Defensive: most rosters lock a parent only when every child is locked, so a live child cannot
        // reach a locked parent. This one does it the other way round to prove the guard is real.
        ISkillRoster roster = SkillRoster.Of(2).Lock(0).Parent(1, 0).Build();
        var book = new SkillBook(roster, TestSkills.Curve);

        SkillAwardResult award = SkillAwards.Apply(book, 1, 100d, Half);

        Assert.Equal(100d, book.Xp(1));
        Assert.Equal(0d, book.Xp(0));
        Assert.Equal(-1, award.Parent);
    }

    [Fact]
    public void A_zero_share_pays_the_child_only()
    {
        SkillBook book = TestSkills.Fresh();

        SkillAwardResult award = SkillAwards.Apply(book, TestSkills.Digging, 100d, 0);

        Assert.Equal(100d, book.Xp(TestSkills.Digging));
        Assert.Equal(0d, book.Xp(TestSkills.Gathering));
        // The parent is still NAMED, because it exists, and its total is reported unchanged.
        Assert.Equal(TestSkills.Gathering, award.Parent);
        Assert.False(award.ParentCrossed);
        Assert.Equal(0d, award.ParentXp);
    }

    [Fact]
    public void Presented_names_the_child_when_it_crossed_else_the_parent()
    {
        SkillBook book = TestSkills.Fresh();
        double levelTwo = TestSkills.Curve.XpForLevel(2);

        SkillAwardResult first = SkillAwards.Apply(book, TestSkills.Chopping, levelTwo + 2d, Half);
        Assert.True(first.Crossed);
        Assert.False(first.ParentCrossed);
        Assert.Equal(TestSkills.Chopping, first.Presented(TestSkills.Chopping));

        book.SetXp(TestSkills.Gathering, levelTwo - 5d);
        SkillAwardResult second = SkillAwards.Apply(book, TestSkills.Chopping, 20d, Half);
        Assert.False(second.Crossed);
        Assert.True(second.ParentCrossed);
        Assert.Equal(TestSkills.Gathering, second.Presented(TestSkills.Chopping));

        SkillAwardResult third = SkillAwards.Apply(book, TestSkills.Chopping, 1d, Half);
        Assert.Equal(-1, third.Presented(TestSkills.Chopping));
    }

    [Fact]
    public void A_parent_saturates_at_the_experience_ceiling()
    {
        SkillBook book = TestSkills.Fresh();
        book.SetXp(TestSkills.Gathering, SkillXpLimits.MaxXp - 1d);

        SkillAwardResult award = SkillAwards.Apply(book, TestSkills.Chopping, 100d, Half);

        Assert.Equal(SkillXpLimits.MaxXp, book.Xp(TestSkills.Gathering));
        Assert.Equal(SkillXpLimits.MaxXp, award.ParentXp);
    }

    [Fact]
    public void The_share_is_basis_points_of_the_denominator()
    {
        // Integer basis points rather than a float fraction, because a published tuning number has to mean
        // the same thing on every machine.
        Assert.Equal(10_000, SkillAwards.ShareDenominator);
        Assert.Equal(50d, SkillAwards.ParentShare(100d, 5000));
        Assert.Equal(1.5d, SkillAwards.ParentShare(100d, 150));
        Assert.Equal(100d, SkillAwards.ParentShare(100d, SkillAwards.ShareDenominator));

        SkillBook book = TestSkills.Fresh();
        SkillAwards.Apply(book, TestSkills.Digging, 100d, 150);
        Assert.Equal(SkillAwards.ParentShare(100d, 150), book.Xp(TestSkills.Gathering));
    }

    [Fact]
    public void Apply_refuses_an_index_outside_the_roster()
    {
        SkillBook book = TestSkills.Fresh();

        Assert.Throws<ArgumentNullException>(() => _ = SkillAwards.Apply(null!, 0, 1d, Half));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _ = SkillAwards.Apply(book, TestSkills.Count, 1d, Half));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = SkillAwards.Apply(book, -1, 1d, Half));
    }
}
