using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// What the builder STAGES against what a draft can actually hold. The two have to agree edit for edit,
/// because a draft holds one pending intent per row: a plan that carried two edits of one row would lose
/// one of them at the store and then fail its own recovery check forever after an interruption.
/// <para>
/// So every verb combination on ONE row is either composed into the single edit the change set will hold,
/// or refused by the builder while nothing has been written.
/// </para>
/// </summary>
public sealed class ContentUpgradePlanBuilderTests
{
    /// <summary>The baseline row every patch and retire here names.</summary>
    static ContentKey OldRow => new("old_row");

    /// <summary>A second baseline row, so a two-row plan is not two edits of one row.</summary>
    static ContentKey SecondRow => new("second_row");

    /// <summary>The committed row an additive upgrade brings.</summary>
    static ContentKey NewRow => new("new_row");

    /// <summary>Two patches of ONE row are one update carrying both fields, which is all a draft can hold.</summary>
    [Fact]
    public void TwoPatchesOfOneRowAreOneUpdateCarryingBothFields()
    {
        ContentUpgradePlan plan = Builder()
            .PatchField(UpgradeFixtures.Thing, OldRow, PublishFixtures.ValueField, Int(11), Int(12))
            .PatchField(UpgradeFixtures.Thing, OldRow, PublishFixtures.LegacyField, AbsentBool, Bool(true))
            .Build();

        Assert.Equal(ContentUpgradePlanKind.Changes, plan.Kind);
        ContentEdit only = Assert.Single(plan.Edits);
        Assert.Equal(ContentEditOperation.Update, only.Operation);
        Assert.Equal(1, only.DefinitionId);
        Assert.Equal(
            [PublishFixtures.ValueField, PublishFixtures.LegacyField],
            Names(only.Fields));
        Assert.Equal(Int(12), only.Fields[0].Value);
        Assert.Equal(Bool(true), only.Fields[1].Value);
        Assert.Equal(2, plan.ChangeLines.Count);
    }

    /// <summary>
    /// The same FIELD twice is the planner contradicting itself, and the builder says which row and which
    /// field rather than publishing whichever value came last.
    /// </summary>
    [Fact]
    public void PatchingOneFieldOfOneRowTwiceIsRefusedNamingTheRowAndTheField()
    {
        ContentUpgradePlanBuilder builder = Builder()
            .PatchField(UpgradeFixtures.Thing, OldRow, PublishFixtures.ValueField, Int(11), Int(12));

        ArgumentException refused = Assert.Throws<ArgumentException>(() => builder.PatchField(
            UpgradeFixtures.Thing, OldRow, PublishFixtures.ValueField, Int(12), Int(13)));

        Assert.Contains("old_row", refused.Message, StringComparison.Ordinal);
        Assert.Contains(PublishFixtures.ValueField, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A patch and a retire of one row are two OPERATIONS on one target, which the change set refuses
    /// outright. The builder says so while nothing has been written.
    /// </summary>
    [Fact]
    public void PatchingAndRetiringOneRowIsRefusedNamingTheRow()
    {
        ContentUpgradePlanBuilder builder = Builder()
            .PatchField(UpgradeFixtures.Thing, OldRow, PublishFixtures.ValueField, Int(11), Int(12));

        ArgumentException refused = Assert.Throws<ArgumentException>(() => builder.RetireRow(
            UpgradeFixtures.Thing, OldRow, ContentRetirePolicy.Placeholder));

        Assert.Contains("old_row", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A retire and then a patch is the same collision from the other side.</summary>
    [Fact]
    public void RetiringAndThenPatchingOneRowIsRefusedNamingTheRow()
    {
        ContentUpgradePlanBuilder builder = Builder()
            .RetireRow(UpgradeFixtures.Thing, OldRow, ContentRetirePolicy.Placeholder);

        Assert.Throws<ArgumentException>(() => builder.PatchField(
            UpgradeFixtures.Thing, OldRow, PublishFixtures.ValueField, Int(11), Int(12)));
    }

    /// <summary>
    /// One row retired twice is one edit at the store and two in the plan, so the comparison the recovery
    /// turns on would never match again. It is refused.
    /// </summary>
    [Fact]
    public void RetiringOneRowTwiceIsRefusedNamingTheRow()
    {
        ContentUpgradePlanBuilder builder = Builder()
            .RetireRow(UpgradeFixtures.Thing, OldRow, ContentRetirePolicy.Placeholder);

        ArgumentException refused = Assert.Throws<ArgumentException>(() => builder.RetireRow(
            UpgradeFixtures.Thing, OldRow, ContentRetirePolicy.Placeholder));

        Assert.Contains("old_row", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>One identity staged twice is the same hazard on the additive half.</summary>
    [Fact]
    public void AddingOneRowTwiceIsRefusedNamingTheRow()
    {
        ContentUpgradePlanBuilder builder = Builder()
            .AddRow(UpgradeFixtures.Other, NewRow);

        ArgumentException refused = Assert.Throws<ArgumentException>(
            () => builder.AddRow(UpgradeFixtures.Other, NewRow));

        Assert.Contains("new_row", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row this plan ADDS is not in the catalog, so there is nothing to patch on it yet. The values it is
    /// created with are the committed bundle's, and the refusal says so rather than staging a second edit.
    /// </summary>
    [Fact]
    public void AddingARowAndThenPatchingItIsRefused()
    {
        ContentUpgradePlan plan = Builder()
            .AddRow(UpgradeFixtures.Other, NewRow)
            .PatchField(UpgradeFixtures.Other, NewRow, PublishFixtures.ValueField, Int(22), Int(23))
            .Build();

        Assert.Equal(ContentUpgradePlanKind.Refused, plan.Kind);
        Assert.Contains("new_row", plan.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole invariant in one place: EVERY shape the builder can produce round trips through a store as
    /// the plan that produced it. This is the comparison an interrupted run's recovery turns on, so a plan
    /// the store cannot hold verbatim is a draft nobody can ever recover.
    /// </summary>
    [Fact]
    public async Task EveryShapeTheBuilderProducesIsADraftTheMatchAccepts()
    {
        foreach ((string what, ContentUpgradePlan plan) in EveryShape())
        {
            Assert.Equal(ContentUpgradePlanKind.Changes, plan.Kind);
            var store = new InMemoryContentAuthoringStore(Registry());
            await store.ApplyEditsAsync(
                plan.Edits, UpgradeFixtures.Actor, UpgradeFixtures.Operator, "round trip");

            ContentDraft draft = Assert.IsType<ContentDraft>(await store.GetOpenDraftAsync());
            Assert.Equal(plan.Edits.Count, draft.EditCount);
            Assert.True(ContentUpgradeDraftMatch.IsPlan(draft, plan.Edits), what);
        }
    }

    /// <summary>Every plan shape worth round tripping, each with the label a failure names it by.</summary>
    static IEnumerable<(string What, ContentUpgradePlan Plan)> EveryShape()
    {
        yield return ("one add", Builder()
            .AddRow(UpgradeFixtures.Other, NewRow)
            .Build());
        yield return ("two adds", Builder()
            .AddRow(UpgradeFixtures.Other, NewRow)
            .AddRow(UpgradeFixtures.Other, new ContentKey("second_new_row"))
            .Build());
        yield return ("one patch", Builder()
            .PatchField(UpgradeFixtures.Thing, OldRow, PublishFixtures.ValueField, Int(11), Int(12))
            .Build());
        yield return ("two patches of one row", Builder()
            .PatchField(UpgradeFixtures.Thing, OldRow, PublishFixtures.ValueField, Int(11), Int(12))
            .PatchField(UpgradeFixtures.Thing, OldRow, PublishFixtures.LegacyField, AbsentBool, Bool(true))
            .Build());
        yield return ("two patches of two rows", Builder()
            .PatchField(UpgradeFixtures.Thing, OldRow, PublishFixtures.ValueField, Int(11), Int(12))
            .PatchField(UpgradeFixtures.Thing, SecondRow, PublishFixtures.ValueField, Int(22), Int(23))
            .Build());
        yield return ("one retire", Builder()
            .RetireRow(UpgradeFixtures.Thing, SecondRow, ContentRetirePolicy.Placeholder)
            .Build());
        yield return ("an add, two patches of one row and a retire", Builder()
            .AddRow(UpgradeFixtures.Other, NewRow)
            .PatchField(UpgradeFixtures.Thing, OldRow, PublishFixtures.ValueField, Int(11), Int(12))
            .PatchField(UpgradeFixtures.Thing, OldRow, PublishFixtures.LegacyField, AbsentBool, Bool(true))
            .RetireRow(UpgradeFixtures.Thing, SecondRow, ContentRetirePolicy.Replacement, 1)
            .Build());
    }

    /// <summary>The current build's registry, carrying both fixture types.</summary>
    static ContentTypeRegistry Registry()
        => PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);

    /// <summary>A builder over the older catalog and this build's committed bundle.</summary>
    static ContentUpgradePlanBuilder Builder()
    {
        ContentTypeRegistry registry = Registry();
        ContentBundle baseline = UpgradeFixtures.Target(
            registry,
            UpgradeFixtures.Row(UpgradeFixtures.Thing, 1, "old_row", 11),
            UpgradeFixtures.Row(UpgradeFixtures.Thing, 2, "second_row", 22));
        ContentBundle target = UpgradeFixtures.Target(
            registry,
            UpgradeFixtures.Row(UpgradeFixtures.Thing, 1, "old_row", 12),
            UpgradeFixtures.Row(UpgradeFixtures.Thing, 2, "second_row", 22),
            UpgradeFixtures.Row(UpgradeFixtures.Other, 1, "new_row", 22),
            UpgradeFixtures.Row(UpgradeFixtures.Other, 2, "second_new_row", 33));
        return new ContentUpgradePlanBuilder(new ContentUpgradeContext(1, baseline, registry), target);
    }

    static ContentFieldValue Int(int value)
        => ContentFieldValue.OfNumber(ContentFieldKind.Int, value);

    static ContentFieldValue Bool(bool value)
        => ContentFieldValue.OfNumber(ContentFieldKind.Bool, value ? 1 : 0);

    static ContentFieldValue AbsentBool => ContentFieldValue.Absent(ContentFieldKind.Bool);

    static string[] Names(IReadOnlyList<ContentFieldEdit> fields)
    {
        var names = new string[fields.Count];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = fields[i].Name;
        }

        return names;
    }
}
