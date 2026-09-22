using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// The LIST half of the planner's value rule. A patch replaces a value only while it still equals a named
/// old default, which is right for a scalar and wrong for a list: an operator who added one tag of their own
/// left the field equal to no default this build ever shipped, and a build that patched it would skip that
/// row forever.
/// <para>
/// So the append detects by ELEMENT, writes the existing list with the element at the END, and counts
/// satisfied against pending exactly the way the other verbs do, which is what lets a build say "these rows
/// each get this tag, all of them or none".
/// </para>
/// </summary>
public sealed class ContentUpgradeTagAppendTests
{
    /// <summary>The committed row an additive upgrade brings, which the baseline holds no row under.</summary>
    const string NewRow = "new_row";

    /// <summary>A tag id no row of the vocabulary holds, live or retired.</summary>
    const int UnknownTag = 99;

    /// <summary>The tag row this build ADDS, which the baseline's vocabulary stops one short of.</summary>
    const int AddedTag = TagUpgradeFixtures.TagCount + 1;

    /// <summary>
    /// The case the whole verb exists for: an operator added their own tag, so the field equals no shipped
    /// default. The append still lands, every element the operator wrote keeps its place, and the new one
    /// goes at the END rather than anywhere a sort would have put it.
    /// </summary>
    [Fact]
    public void AnOperatorsOwnTagsAndTheirOrderSurviveTheAppend()
    {
        ContentUpgradePlan plan = Builder()
            .AppendTag(
                TagUpgradeFixtures.Tagged,
                new ContentKey(TagUpgradeFixtures.TunedRow),
                TagUpgradeFixtures.TagsField,
                TagUpgradeFixtures.NewTag)
            .Build();

        Assert.Equal(ContentUpgradePlanKind.Changes, plan.Kind);
        ContentEdit only = Assert.Single(plan.Edits);
        Assert.Equal(ContentEditOperation.Update, only.Operation);
        Assert.Equal(2, only.DefinitionId);
        ContentFieldEdit field = Assert.Single(only.Fields);
        Assert.Equal(TagUpgradeFixtures.TagsField, field.Name);
        Assert.Equal(
            [TagUpgradeFixtures.ShippedTag, TagUpgradeFixtures.OperatorTag, TagUpgradeFixtures.NewTag],
            Tags(field.Value));
    }

    /// <summary>
    /// A row whose list already holds the element is SATISFIED, which is what makes a re-run a no-op. Every
    /// row satisfied is the already-satisfied plan the runner records as adopted, with no edit at all.
    /// </summary>
    [Fact]
    public void ATagAlreadyInEveryListIsSatisfiedAndEmitsNoEdit()
    {
        ContentUpgradePlan plan = AppendToBoth(
            TagUpgradeFixtures.Row(1, TagUpgradeFixtures.PlainRow, 11, TagUpgradeFixtures.ShippedTag, TagUpgradeFixtures.NewTag),
            TagUpgradeFixtures.Row(2, TagUpgradeFixtures.TunedRow, 22, TagUpgradeFixtures.ShippedTag, TagUpgradeFixtures.OperatorTag, TagUpgradeFixtures.NewTag));

        Assert.Equal(ContentUpgradePlanKind.AlreadySatisfied, plan.Kind);
        Assert.Empty(plan.Edits);
    }

    /// <summary>
    /// A build says "these rows each get this tag, all of them or none". One row holding it and one not is a
    /// MIXED state, and the append reaches the builder's one partial-state rule by counting satisfied against
    /// pending the way every other verb does rather than by a mechanism of its own.
    /// </summary>
    [Fact]
    public void AMixedStateAcrossRowsIsRefusedRatherThanCompleted()
    {
        ContentUpgradePlan plan = AppendToBoth(
            TagUpgradeFixtures.Row(1, TagUpgradeFixtures.PlainRow, 11, TagUpgradeFixtures.ShippedTag, TagUpgradeFixtures.NewTag),
            TagUpgradeFixtures.Row(2, TagUpgradeFixtures.TunedRow, 22, TagUpgradeFixtures.ShippedTag, TagUpgradeFixtures.OperatorTag));

        Assert.Equal(ContentUpgradePlanKind.Refused, plan.Kind);
        Assert.Contains("partial", plan.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// An append and a patch of ANOTHER field on one row are one update carrying both, which is all a draft
    /// can hold. The two land in the order they were staged.
    /// </summary>
    [Fact]
    public void AnAppendAndAPatchOfAnotherFieldOnOneRowAreOneUpdate()
    {
        ContentUpgradePlan plan = Builder()
            .AppendTag(
                TagUpgradeFixtures.Tagged,
                new ContentKey(TagUpgradeFixtures.PlainRow),
                TagUpgradeFixtures.TagsField,
                TagUpgradeFixtures.NewTag)
            .PatchField(
                TagUpgradeFixtures.Tagged,
                new ContentKey(TagUpgradeFixtures.PlainRow),
                PublishFixtures.ValueField,
                Int(11),
                Int(12))
            .Build();

        ContentEdit only = Assert.Single(plan.Edits);
        Assert.Equal(ContentEditOperation.Update, only.Operation);
        Assert.Equal(
            [TagUpgradeFixtures.TagsField, PublishFixtures.ValueField],
            Names(only.Fields));
        Assert.Equal(
            [TagUpgradeFixtures.ShippedTag, TagUpgradeFixtures.NewTag],
            Tags(only.Fields[0].Value));
        Assert.Equal(Int(12), only.Fields[1].Value);
        Assert.Equal(2, plan.ChangeLines.Count);
    }

    /// <summary>
    /// One FIELD written twice by one plan is the planner contradicting itself, whether the second call
    /// appends the same element, a different one, or patches the whole value. A row carries one value per
    /// field, so there is no answer the draft could keep.
    /// </summary>
    [Theory]
    [InlineData(TagUpgradeFixtures.NewTag)]
    [InlineData(TagUpgradeFixtures.NewTag - 1)]
    public void AppendingToOneFieldTwiceIsRefusedNamingTheRowAndTheField(int second)
    {
        ContentUpgradePlanBuilder builder = Builder()
            .AppendTag(
                TagUpgradeFixtures.Tagged,
                new ContentKey(TagUpgradeFixtures.PlainRow),
                TagUpgradeFixtures.TagsField,
                TagUpgradeFixtures.NewTag);

        ArgumentException refused = Assert.Throws<ArgumentException>(() => builder.AppendTag(
            TagUpgradeFixtures.Tagged,
            new ContentKey(TagUpgradeFixtures.PlainRow),
            TagUpgradeFixtures.TagsField,
            second));

        Assert.Contains(TagUpgradeFixtures.PlainRow, refused.Message, StringComparison.Ordinal);
        Assert.Contains(TagUpgradeFixtures.TagsField, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A patch of the same field is the same collision, and it reads the same from either side.</summary>
    [Fact]
    public void AppendingToAFieldThisPlanAlsoPatchesIsRefusedFromEitherSide()
    {
        ContentUpgradePlanBuilder appendFirst = Builder()
            .AppendTag(
                TagUpgradeFixtures.Tagged,
                new ContentKey(TagUpgradeFixtures.PlainRow),
                TagUpgradeFixtures.TagsField,
                TagUpgradeFixtures.NewTag);

        Assert.Throws<ArgumentException>(() => appendFirst.PatchField(
            TagUpgradeFixtures.Tagged,
            new ContentKey(TagUpgradeFixtures.PlainRow),
            TagUpgradeFixtures.TagsField,
            ContentRowCodecBase.TagListValue([TagUpgradeFixtures.ShippedTag]),
            ContentRowCodecBase.TagListValue([TagUpgradeFixtures.NewTag])));

        ContentUpgradePlanBuilder patchFirst = Builder()
            .PatchField(
                TagUpgradeFixtures.Tagged,
                new ContentKey(TagUpgradeFixtures.PlainRow),
                TagUpgradeFixtures.TagsField,
                ContentRowCodecBase.TagListValue([TagUpgradeFixtures.ShippedTag]),
                ContentRowCodecBase.TagListValue([TagUpgradeFixtures.NewTag]));

        ArgumentException refused = Assert.Throws<ArgumentException>(() => patchFirst.AppendTag(
            TagUpgradeFixtures.Tagged,
            new ContentKey(TagUpgradeFixtures.PlainRow),
            TagUpgradeFixtures.TagsField,
            TagUpgradeFixtures.NewTag));

        Assert.Contains(TagUpgradeFixtures.TagsField, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An append and a retire are two OPERATIONS on one target, which a change set holds one of. The builder
    /// says so from either side while nothing has been written.
    /// </summary>
    [Fact]
    public void AppendingToARowThisPlanRetiresIsRefusedFromEitherSide()
    {
        ContentUpgradePlanBuilder appendFirst = Builder()
            .AppendTag(
                TagUpgradeFixtures.Tagged,
                new ContentKey(TagUpgradeFixtures.PlainRow),
                TagUpgradeFixtures.TagsField,
                TagUpgradeFixtures.NewTag);

        ArgumentException refused = Assert.Throws<ArgumentException>(() => appendFirst.RetireRow(
            TagUpgradeFixtures.Tagged, new ContentKey(TagUpgradeFixtures.PlainRow), ContentRetirePolicy.Placeholder));

        Assert.Contains(TagUpgradeFixtures.PlainRow, refused.Message, StringComparison.Ordinal);

        ContentUpgradePlanBuilder retireFirst = Builder()
            .RetireRow(
                TagUpgradeFixtures.Tagged, new ContentKey(TagUpgradeFixtures.PlainRow), ContentRetirePolicy.Placeholder);

        Assert.Throws<ArgumentException>(() => retireFirst.AppendTag(
            TagUpgradeFixtures.Tagged,
            new ContentKey(TagUpgradeFixtures.PlainRow),
            TagUpgradeFixtures.TagsField,
            TagUpgradeFixtures.NewTag));
    }

    /// <summary>
    /// A row this plan ADDS is not in the catalog, so it carries no list to append to yet. The values it is
    /// created with are the committed bundle's, and the refusal says the catalog does not hold the row rather
    /// than staging a second edit of it.
    /// </summary>
    [Fact]
    public void AppendingToARowThisPlanAddsIsRefused()
    {
        ContentUpgradePlan plan = Builder()
            .AddRow(TagUpgradeFixtures.Tagged, new ContentKey(NewRow))
            .AppendTag(
                TagUpgradeFixtures.Tagged,
                new ContentKey(NewRow),
                TagUpgradeFixtures.TagsField,
                TagUpgradeFixtures.NewTag)
            .Build();

        Assert.Equal(ContentUpgradePlanKind.Refused, plan.Kind);
        Assert.Contains(NewRow, plan.Reason, StringComparison.Ordinal);
    }

    /// <summary>A field the catalog holds no row for is the same refusal every verb gives.</summary>
    [Fact]
    public void AppendingToARowTheCatalogDoesNotHoldIsRefused()
    {
        ContentUpgradePlan plan = Builder()
            .AppendTag(
                TagUpgradeFixtures.Tagged,
                new ContentKey("absent_row"),
                TagUpgradeFixtures.TagsField,
                TagUpgradeFixtures.NewTag)
            .Build();

        Assert.Equal(ContentUpgradePlanKind.Refused, plan.Kind);
        Assert.Contains("absent_row", plan.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Appending to a scalar is a category error the builder answers rather than a value it invents. The
    /// refusal names the kind the schema declares, so a planner sees which of the two verbs it wanted.
    /// </summary>
    [Fact]
    public void AppendingToAFieldThatIsNotATagListIsRefused()
    {
        ContentUpgradePlan plan = Builder()
            .AppendTag(
                TagUpgradeFixtures.Tagged,
                new ContentKey(TagUpgradeFixtures.PlainRow),
                PublishFixtures.ValueField,
                TagUpgradeFixtures.NewTag)
            .Build();

        Assert.Equal(ContentUpgradePlanKind.Refused, plan.Kind);
        Assert.Contains(PublishFixtures.ValueField, plan.Reason, StringComparison.Ordinal);
        Assert.Contains("tag list", plan.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tag list names tag ROWS rather than strings, so an id the catalog holds no row for is refused at
    /// PLAN time naming the row, the field and the id. Staging it would publish a list the validator answers
    /// with <c>KEC0008</c>, which throws at the store and ends the run failed rather than refused.
    /// </summary>
    [Fact]
    public void ATagIdTheCatalogHoldsNoRowForIsRefused()
    {
        ContentUpgradePlan plan = Builder()
            .AppendTag(
                TagUpgradeFixtures.Tagged,
                new ContentKey(TagUpgradeFixtures.PlainRow),
                TagUpgradeFixtures.TagsField,
                UnknownTag)
            .Build();

        Assert.Equal(ContentUpgradePlanKind.Refused, plan.Kind);
        Assert.Empty(plan.Edits);
        Assert.Contains(TagUpgradeFixtures.PlainRow, plan.Reason, StringComparison.Ordinal);
        Assert.Contains(TagUpgradeFixtures.TagsField, plan.Reason, StringComparison.Ordinal);
        Assert.Contains("tag row 99", plan.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A RETIRED tag row is refused the same way, because that is exactly what the publish validator would
    /// reject: <c>KEC0008</c> accepts a list entry only when the tag row is present AND live, and a retired
    /// row keeps its id and its bytes forever without being live.
    /// </summary>
    [Fact]
    public void ARetiredTagRowIsRefusedTheWayTheValidatorRejectsIt()
    {
        ContentUpgradePlan plan = BuilderOver(TagUpgradeFixtures.Vocabulary(TagUpgradeFixtures.NewTag))
            .AppendTag(
                TagUpgradeFixtures.Tagged,
                new ContentKey(TagUpgradeFixtures.PlainRow),
                TagUpgradeFixtures.TagsField,
                TagUpgradeFixtures.NewTag)
            .Build();

        Assert.Equal(ContentUpgradePlanKind.Refused, plan.Kind);
        Assert.Empty(plan.Edits);
        Assert.Contains(TagUpgradeFixtures.PlainRow, plan.Reason, StringComparison.Ordinal);
        Assert.Contains("tag row 3", plan.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Adding the tag row and appending it in ONE definition is legitimate, so the id resolves against the
    /// rows this plan adds as well as the rows the baseline holds. It is answered at <c>Build</c> time, which
    /// is what makes the two call orders read the same.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATagRowThisPlanAddsResolvesInEitherCallOrder(bool addFirst)
    {
        ContentUpgradePlanBuilder builder = Builder();
        if (addFirst)
        {
            builder.AddRow(TagUpgradeFixtures.Tag, TagUpgradeFixtures.TagKey(AddedTag));
        }

        builder.AppendTag(
            TagUpgradeFixtures.Tagged,
            new ContentKey(TagUpgradeFixtures.PlainRow),
            TagUpgradeFixtures.TagsField,
            AddedTag);

        if (!addFirst)
        {
            builder.AddRow(TagUpgradeFixtures.Tag, TagUpgradeFixtures.TagKey(AddedTag));
        }

        ContentUpgradePlan plan = builder.Build();

        Assert.Equal(ContentUpgradePlanKind.Changes, plan.Kind);
        Assert.Equal(2, plan.Edits.Count);
        Assert.Equal(ContentEditOperation.Add, plan.Edits[0].Operation);
        Assert.Equal(
            [TagUpgradeFixtures.ShippedTag, AddedTag],
            Tags(plan.Edits[1].Fields[0].Value));
    }

    /// <summary>
    /// The refusal reaches the RUNNER as a refusal rather than as a failed publish: no draft is left open
    /// and no version is published, which is the whole difference this check buys.
    /// </summary>
    [Fact]
    public async Task AnUnknownTagIdStopsTheRunWithNoDraftAndNoVersion()
    {
        using var files = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = TagUpgradeFixtures.Registry();
        var store = new InMemoryContentAuthoringStore(registry, files.Pack());
        await TagUpgradeFixtures.SeedAsync(store);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            store,
            registry,
            new ContentUpgradeSet(TagUpgradeFixtures.AppendsTag(
                UpgradeFixtures.Target(registry), UnknownTag, TagUpgradeFixtures.PlainRow)),
            UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Refused, report.Outcome);
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Single(await store.ListVersionsAsync());
        Assert.Empty(await store.ListUpgradesAsync());
    }

    /// <summary>
    /// A row carrying NO tag list at all is appended to as an empty one. A codec leaves an empty list off
    /// the row entirely, so absence is the ordinary shape of a row with no tags rather than a defect, and the
    /// append writes the one-element list a row authored with that tag would carry.
    /// </summary>
    [Fact]
    public Task AppendingToARowWhoseListIsAbsentWritesAOneElementList()
        => AssertOneElementListAsync(TagUpgradeFixtures.Row(1, TagUpgradeFixtures.PlainRow, 11));

    /// <summary>
    /// A row carrying a list that is PRESENT and EMPTY is the other zero-tag shape, and it reads the same
    /// way. The two are separate cases because the value carries a flag for the first and a length for the
    /// second, and nothing but a test says they agree.
    /// </summary>
    [Fact]
    public Task AppendingToARowWhoseListIsEmptyWritesAOneElementList()
        => AssertOneElementListAsync(TagUpgradeFixtures.EmptyListRow(1, TagUpgradeFixtures.PlainRow, 11));

    /// <summary>A field the type does not declare at all is the caller's own typo, and it is refused.</summary>
    [Fact]
    public void AppendingToAFieldTheTypeDoesNotDeclareIsRefused()
    {
        ContentUpgradePlan plan = Builder()
            .AppendTag(
                TagUpgradeFixtures.Tagged,
                new ContentKey(TagUpgradeFixtures.PlainRow),
                "no_such_field",
                TagUpgradeFixtures.NewTag)
            .Build();

        Assert.Equal(ContentUpgradePlanKind.Refused, plan.Kind);
        Assert.Contains("no_such_field", plan.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The invariant the recovery turns on: an append plan is a draft the match accepts back. It holds on
    /// both providers, because the draft is read back out of the store rather than out of the plan.
    /// </summary>
    [Fact]
    public async Task AnAppendPlanRoundTripsThroughTheInMemoryStoreAsTheMatchAccepts()
    {
        ContentTypeRegistry registry = TagUpgradeFixtures.Registry();
        var store = new InMemoryContentAuthoringStore(registry);
        await AssertRoundTripsAsync(store);
    }

    /// <inheritdoc cref="AnAppendPlanRoundTripsThroughTheInMemoryStoreAsTheMatchAccepts"/>
    [Fact]
    public async Task AnAppendPlanRoundTripsThroughTheSqliteStoreAsTheMatchAccepts()
    {
        using var files = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(
            files.ConnectionString, TagUpgradeFixtures.Registry(), files.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await AssertRoundTripsAsync(store);
    }

    /// <summary>
    /// A run killed between its edits and its publish leaves a draft holding exactly this plan. The next run
    /// publishes THAT draft, and the catalog ends at one version with the operator's own tag still in the
    /// place they wrote it.
    /// </summary>
    [Fact]
    public async Task AnInterruptedAppendRunRecoversAsExactlyOneVersion()
    {
        using var files = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = TagUpgradeFixtures.Registry();
        var store = new InMemoryContentAuthoringStore(registry, files.Pack());
        await TagUpgradeFixtures.SeedAsync(store);
        ContentUpgradeDefinition definition = TagUpgradeFixtures.AppendsTag(
            UpgradeFixtures.Target(registry), TagUpgradeFixtures.PlainRow, TagUpgradeFixtures.TunedRow);

        int active = await store.GetActiveVersionAsync();
        ContentBundle baseline = await store.ExportBundleAsync(active);
        await store.ApplyEditsAsync(
            definition.Plan(new ContentUpgradeContext(active, baseline, registry)).Edits,
            UpgradeFixtures.Actor,
            UpgradeFixtures.Operator,
            ContentUpgradeRunner.NoteFor(TagUpgradeFixtures.UpgradeId));

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            store, registry, new ContentUpgradeSet(definition), UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Equal(2, report.ActiveVersionAfter);
        Assert.Equal(2, (await store.ListVersionsAsync()).Count);
        Assert.Single(await store.ListUpgradesAsync());
        Assert.Null(await store.GetOpenDraftAsync());

        int[] plain = await TagUpgradeFixtures.TagsOfAsync(store, TagUpgradeFixtures.PlainRow);
        int[] tuned = await TagUpgradeFixtures.TagsOfAsync(store, TagUpgradeFixtures.TunedRow);

        Assert.Equal([TagUpgradeFixtures.ShippedTag, TagUpgradeFixtures.NewTag], plain);
        Assert.Equal(
            [TagUpgradeFixtures.ShippedTag, TagUpgradeFixtures.OperatorTag, TagUpgradeFixtures.NewTag],
            tuned);
    }

    /// <summary>
    /// The append on a row that carries no tag yet, in whichever of the two zero-tag shapes the caller
    /// states: one element in the plan, and the same list still there after a store has held it as a draft.
    /// </summary>
    /// <param name="baselineRow">The tagged row the catalog holds.</param>
    static async Task AssertOneElementListAsync(ContentBundleRow baselineRow)
    {
        ContentUpgradePlan plan = Builder(baselineRow)
            .AppendTag(
                TagUpgradeFixtures.Tagged,
                new ContentKey(TagUpgradeFixtures.PlainRow),
                TagUpgradeFixtures.TagsField,
                TagUpgradeFixtures.NewTag)
            .Build();

        Assert.Equal(ContentUpgradePlanKind.Changes, plan.Kind);
        ContentEdit only = Assert.Single(plan.Edits);
        Assert.Equal(ContentEditOperation.Update, only.Operation);
        ContentFieldEdit field = Assert.Single(only.Fields);
        Assert.Equal(TagUpgradeFixtures.TagsField, field.Name);
        Assert.Equal([TagUpgradeFixtures.NewTag], Tags(field.Value));

        var store = new InMemoryContentAuthoringStore(TagUpgradeFixtures.Registry());
        await store.ApplyEditsAsync(
            plan.Edits, UpgradeFixtures.Actor, UpgradeFixtures.Operator, "one element list");

        ContentDraft draft = Assert.IsType<ContentDraft>(await store.GetOpenDraftAsync());
        Assert.True(ContentUpgradeDraftMatch.IsPlan(draft, plan.Edits));
    }

    /// <summary>The append plan applied to a store and read back as the draft the match accepts.</summary>
    /// <param name="store">The store under test, already open.</param>
    static async Task AssertRoundTripsAsync(IContentAuthoringStore store)
    {
        ContentUpgradePlan plan = Builder()
            .AppendTag(
                TagUpgradeFixtures.Tagged,
                new ContentKey(TagUpgradeFixtures.PlainRow),
                TagUpgradeFixtures.TagsField,
                TagUpgradeFixtures.NewTag)
            .PatchField(
                TagUpgradeFixtures.Tagged,
                new ContentKey(TagUpgradeFixtures.PlainRow),
                PublishFixtures.ValueField,
                Int(11),
                Int(12))
            .AppendTag(
                TagUpgradeFixtures.Tagged,
                new ContentKey(TagUpgradeFixtures.TunedRow),
                TagUpgradeFixtures.TagsField,
                TagUpgradeFixtures.NewTag)
            .Build();

        Assert.Equal(ContentUpgradePlanKind.Changes, plan.Kind);
        await store.ApplyEditsAsync(
            plan.Edits, UpgradeFixtures.Actor, UpgradeFixtures.Operator, "round trip");

        ContentDraft draft = Assert.IsType<ContentDraft>(await store.GetOpenDraftAsync());
        Assert.Equal(plan.Edits.Count, draft.EditCount);
        Assert.True(ContentUpgradeDraftMatch.IsPlan(draft, plan.Edits));
    }

    /// <summary>The same append on both seeded rows, over a baseline the caller states row by row.</summary>
    /// <param name="baselineRows">The tagged rows the catalog holds.</param>
    static ContentUpgradePlan AppendToBoth(params ContentBundleRow[] baselineRows)
    {
        ContentUpgradePlanBuilder builder = Builder(baselineRows);
        return builder
            .AppendTag(
                TagUpgradeFixtures.Tagged,
                new ContentKey(TagUpgradeFixtures.PlainRow),
                TagUpgradeFixtures.TagsField,
                TagUpgradeFixtures.NewTag)
            .AppendTag(
                TagUpgradeFixtures.Tagged,
                new ContentKey(TagUpgradeFixtures.TunedRow),
                TagUpgradeFixtures.TagsField,
                TagUpgradeFixtures.NewTag)
            .Build();
    }

    /// <summary>
    /// A builder over the named baseline rows, or over the two seeded ones when the caller names none, and
    /// this build's committed bundle, which carries one row the baseline does not. The baseline's tag
    /// vocabulary is live, which is what an append's id has to resolve against.
    /// </summary>
    /// <param name="baselineRows">The tagged rows the catalog holds.</param>
    static ContentUpgradePlanBuilder Builder(params ContentBundleRow[] baselineRows)
        => BuilderOver(TagUpgradeFixtures.Vocabulary(), baselineRows);

    /// <summary>
    /// The same builder over a STATED vocabulary, so a test may say that one of the tags is retired. The
    /// committed bundle always carries one tag row past the baseline's, which is the row an upgrade adds and
    /// appends in the same definition.
    /// </summary>
    /// <param name="vocabulary">The tag rows the catalog holds.</param>
    /// <param name="baselineRows">The tagged rows the catalog holds.</param>
    static ContentUpgradePlanBuilder BuilderOver(
        ContentBundleRow[] vocabulary,
        params ContentBundleRow[] baselineRows)
    {
        ContentTypeRegistry registry = TagUpgradeFixtures.Registry();
        ContentBundleRow[] rows = baselineRows.Length > 0
            ? baselineRows
            :
            [
                TagUpgradeFixtures.Row(1, TagUpgradeFixtures.PlainRow, 11, TagUpgradeFixtures.ShippedTag),
                TagUpgradeFixtures.Row(
                    2,
                    TagUpgradeFixtures.TunedRow,
                    22,
                    TagUpgradeFixtures.ShippedTag,
                    TagUpgradeFixtures.OperatorTag),
            ];
        ContentBundle baseline = UpgradeFixtures.Target(registry, [.. vocabulary, .. rows]);
        ContentBundle target = UpgradeFixtures.Target(
            registry,
            [
                .. TagUpgradeFixtures.Vocabulary(),
                TagUpgradeFixtures.TagRow(AddedTag),
                TagUpgradeFixtures.Row(
                    1,
                    TagUpgradeFixtures.PlainRow,
                    11,
                    TagUpgradeFixtures.ShippedTag,
                    TagUpgradeFixtures.NewTag),
                TagUpgradeFixtures.Row(
                    2,
                    TagUpgradeFixtures.TunedRow,
                    22,
                    TagUpgradeFixtures.ShippedTag,
                    TagUpgradeFixtures.NewTag),
                TagUpgradeFixtures.Row(3, NewRow, 33, TagUpgradeFixtures.NewTag),
            ]);
        return new ContentUpgradePlanBuilder(new ContentUpgradeContext(1, baseline, registry), target);
    }

    static ContentFieldValue Int(int value)
        => ContentFieldValue.OfNumber(ContentFieldKind.Int, value);

    /// <summary>The tag ids one field value holds, in authored order.</summary>
    /// <param name="value">The tag-list value.</param>
    static int[] Tags(ContentFieldValue value)
    {
        var ids = new int[value.Bytes.Length];
        int count = ContentRowCodecBase.ReadTagList(in value, ids);
        return ids[..count];
    }

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
