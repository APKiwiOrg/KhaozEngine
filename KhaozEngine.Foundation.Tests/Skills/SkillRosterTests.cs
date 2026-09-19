using System;
using KhaozEngine.Skills;
using Xunit;

namespace KhaozEngine.Tests.Foundation.Skills;

/// <summary>The batteries-included roster and its builder: the defaults, the two lock directions, the
/// parent link, and the loop a build refuses.</summary>
public class SkillRosterTests
{
    [Fact]
    public void Everything_starts_open_and_a_root()
    {
        // The shape a game with no tree and no locking wants, and the one that needs no calls at all.
        SkillRoster roster = SkillRoster.Flat(4);

        Assert.Equal(4, roster.Count);
        for (int skill = 0; skill < roster.Count; skill++)
        {
            Assert.False(roster.IsLocked(skill));
            Assert.Equal(-1, roster.ParentOf(skill));
            Assert.False(roster.IsParent(skill));
        }
    }

    [Fact]
    public void LockAll_inverts_the_default_for_a_roster_that_is_mostly_aspiration()
    {
        SkillRoster roster = SkillRoster.Of(3).LockAll().Unlock(1).Build();

        Assert.True(roster.IsLocked(0));
        Assert.False(roster.IsLocked(1));
        Assert.True(roster.IsLocked(2));
    }

    [Fact]
    public void A_parent_link_is_one_way_and_answers_both_questions()
    {
        SkillRoster roster = TestSkills.Roster;

        Assert.Equal(TestSkills.Gathering, roster.ParentOf(TestSkills.Chopping));
        Assert.Equal(-1, roster.ParentOf(TestSkills.Gathering));
        Assert.True(roster.IsParent(TestSkills.Gathering));
        Assert.False(roster.IsParent(TestSkills.Chopping));
        // A root leaf is a root with no children, which is not the same thing as a parent with none.
        Assert.Equal(-1, roster.ParentOf(TestSkills.Vitality));
        Assert.False(roster.IsParent(TestSkills.Vitality));
    }

    [Fact]
    public void An_index_outside_the_roster_answers_locked_and_rootless()
    {
        SkillRoster roster = TestSkills.Roster;

        // Tolerant rather than throwing, because a roster is asked about ids that came off a blob. The
        // book is the type that throws, and by then the id has already been checked.
        Assert.True(roster.IsLocked(-1));
        Assert.True(roster.IsLocked(TestSkills.Count));
        Assert.Equal(-1, roster.ParentOf(-1));
        Assert.Equal(-1, roster.ParentOf(TestSkills.Count));
        // And -1 is not the parent of every root, which an unguarded scan would answer.
        Assert.False(roster.IsParent(-1));
    }

    [Fact]
    public void Parent_minus_one_makes_a_child_a_root_again()
    {
        SkillRoster roster = SkillRoster.Of(2).Parent(1, 0).Parent(1, -1).Build();

        Assert.Equal(-1, roster.ParentOf(1));
        Assert.False(roster.IsParent(0));
    }

    [Fact]
    public void The_builder_refuses_an_index_it_does_not_have()
    {
        SkillRosterBuilder builder = SkillRoster.Of(3);

        Assert.Equal(3, builder.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Lock(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Unlock(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Parent(3, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Parent(0, 3));
        Assert.Throws<ArgumentException>(() => builder.Parent(1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SkillRoster.Of(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SkillRoster.Of(-2));
    }

    [Fact]
    public void A_parent_chain_that_loops_is_refused_at_build()
    {
        // Caught HERE rather than at the first award, because an award walks one step up and would never
        // notice: a two-skill cycle pays each other a share forever without recursing, and the only
        // visible symptom is two skills nobody can explain the level of.
        Assert.Throws<InvalidOperationException>(() => SkillRoster.Of(2).Parent(0, 1).Parent(1, 0).Build());
        Assert.Throws<InvalidOperationException>(() =>
            SkillRoster.Of(3).Parent(0, 1).Parent(1, 2).Parent(2, 0).Build());

        // A chain that reaches a root is fine however deep it goes, and only the immediate parent is paid.
        SkillRoster deep = SkillRoster.Of(3).Parent(0, 1).Parent(1, 2).Build();
        Assert.Equal(1, deep.ParentOf(0));
        Assert.Equal(2, deep.ParentOf(1));
        Assert.Equal(-1, deep.ParentOf(2));
    }

    [Fact]
    public void A_built_roster_does_not_change_under_a_reused_builder()
    {
        SkillRosterBuilder builder = SkillRoster.Of(2);
        SkillRoster first = builder.Build();

        builder.Lock(0).Parent(1, 0);
        SkillRoster second = builder.Build();

        Assert.False(first.IsLocked(0));
        Assert.Equal(-1, first.ParentOf(1));
        Assert.True(second.IsLocked(0));
        Assert.Equal(0, second.ParentOf(1));
    }
}
