using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// The ONE comparison that decides whether an open draft is a runner's own work or an operator's. It is
/// unit tested apart from the runner because everything the runner does to a draft it believes is its own
/// is irreversible, and a comparison that says yes too often destroys unpublished edits.
/// </summary>
public sealed class ContentUpgradeDraftMatchTests
{
    /// <summary>The edits a definition plans, which a draft has to reproduce exactly to be that plan.</summary>
    static IReadOnlyList<ContentEdit> Planned =>
    [
        ContentEdit.Import(UpgradeFixtures.Other, 1, new ContentKey("new_row"), PublishFixtures.Fields(22)),
        ContentEdit.Update(UpgradeFixtures.Thing, 1, new ContentKey("old_row"), PublishFixtures.Fields(33)),
    ];

    /// <summary>The same edits in the same order are the plan.</summary>
    [Fact]
    public void TheSameEditsInTheSameOrderAreThePlan()
        => Assert.True(ContentUpgradeDraftMatch.IsPlan(Draft(Planned), Planned));

    /// <summary>
    /// The comparison is order INDEPENDENT, because a draft is rebuilt from a stored edit table and the
    /// runner has no promise that a provider hands the rows back in the order it wrote them.
    /// </summary>
    [Fact]
    public void TheSameEditsInAnotherOrderAreStillThePlan()
    {
        IReadOnlyList<ContentEdit> planned = Planned;
        Assert.True(ContentUpgradeDraftMatch.IsPlan(Draft([planned[1], planned[0]]), planned));
    }

    /// <summary>An EXTRA edit is an operator's addition, so the draft is no longer the plan.</summary>
    [Fact]
    public void AnExtraEditIsNotThePlan()
    {
        IReadOnlyList<ContentEdit> planned = Planned;
        ContentDraft draft = Draft(
        [
            planned[0],
            planned[1],
            ContentEdit.Add(UpgradeFixtures.Thing, new ContentKey("operator_row"), PublishFixtures.Fields(9)),
        ]);

        Assert.False(ContentUpgradeDraftMatch.IsPlan(draft, planned));
    }

    /// <summary>A REMOVED edit is not the plan either, whichever half is missing.</summary>
    [Fact]
    public void ARemovedEditIsNotThePlan()
    {
        IReadOnlyList<ContentEdit> planned = Planned;
        Assert.False(ContentUpgradeDraftMatch.IsPlan(Draft([planned[0]]), planned));
    }

    /// <summary>A changed FIELD VALUE under an unchanged target is the one an id comparison would miss.</summary>
    [Fact]
    public void AChangedFieldValueIsNotThePlan()
    {
        IReadOnlyList<ContentEdit> planned = Planned;
        ContentDraft draft = Draft(
        [
            planned[0],
            ContentEdit.Update(UpgradeFixtures.Thing, 1, new ContentKey("old_row"), PublishFixtures.Fields(34)),
        ]);

        Assert.False(ContentUpgradeDraftMatch.IsPlan(draft, planned));
    }

    /// <summary>A changed OPERATION on one target is a different edit, not a changed one.</summary>
    [Fact]
    public void AChangedOperationIsNotThePlan()
    {
        IReadOnlyList<ContentEdit> planned = Planned;
        ContentDraft draft = Draft(
        [
            planned[0],
            ContentEdit.Retire(
                UpgradeFixtures.Thing, 1, new ContentKey("old_row"), ContentRetirePolicy.Placeholder, 0),
        ]);

        Assert.False(ContentUpgradeDraftMatch.IsPlan(draft, planned));
    }

    /// <summary>A retire's POLICY and its replacement id are part of the edit, so a change to either shows.</summary>
    [Fact]
    public void AChangedRetirePolicyOrReplacementIsNotThePlan()
    {
        IReadOnlyList<ContentEdit> planned =
        [
            ContentEdit.Retire(
                UpgradeFixtures.Thing, 1, new ContentKey("old_row"), ContentRetirePolicy.Replacement, 2),
        ];

        Assert.False(ContentUpgradeDraftMatch.IsPlan(
            Draft([ContentEdit.Retire(
                UpgradeFixtures.Thing, 1, new ContentKey("old_row"), ContentRetirePolicy.Replacement, 3)]),
            planned));
        Assert.False(ContentUpgradeDraftMatch.IsPlan(
            Draft([ContentEdit.Retire(
                UpgradeFixtures.Thing, 1, new ContentKey("old_row"), ContentRetirePolicy.Placeholder, 0)]),
            planned));
        Assert.True(ContentUpgradeDraftMatch.IsPlan(Draft(planned), planned));
    }

    /// <summary>
    /// One field named TWICE is a set no row can hold, and the answer has to be the same whichever side
    /// carries the duplicate. A check that only looked at the held side would accept a draft holding two
    /// different fields as a plan naming one field twice, which is two edits away from the same row.
    /// </summary>
    [Fact]
    public void ADuplicateFieldNameOnEitherSideIsNotThePlan()
    {
        ContentFieldEdit value = new(
            PublishFixtures.ValueField, ContentFieldValue.OfNumber(ContentFieldKind.Int, 33));
        ContentFieldEdit legacy = new(
            PublishFixtures.LegacyField, ContentFieldValue.OfNumber(ContentFieldKind.Bool, 1));
        ContentEdit two = ContentEdit.Update(
            UpgradeFixtures.Thing, 1, new ContentKey("old_row"), [value, legacy]);
        ContentEdit twice = ContentEdit.Update(
            UpgradeFixtures.Thing, 1, new ContentKey("old_row"), [value, value]);

        Assert.False(ContentUpgradeDraftMatch.IsPlan(Draft([two]), [twice]));
        Assert.False(ContentUpgradeDraftMatch.IsPlan(Draft([twice]), [two]));
    }

    /// <summary>An empty draft against an empty plan is not a match, because a plan is never empty.</summary>
    [Fact]
    public void AnEmptyDraftIsNotThePlan()
        => Assert.False(ContentUpgradeDraftMatch.IsPlan(Draft([]), Planned));

    /// <summary>One draft carrying the given edits, which is the shape a store hands back.</summary>
    static ContentDraft Draft(IReadOnlyList<ContentEdit> edits)
        => new(
            1,
            UpgradeFixtures.Actor,
            System.DateTimeOffset.UnixEpoch,
            ContentUpgradeRunner.NoteFor(UpgradeHarness.FirstId),
            new ContentChangeSet(edits));
}
