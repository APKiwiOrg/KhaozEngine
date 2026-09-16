using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Store;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// The <c>Fork</c> of spec 3.7, the keep-legacy operation that is the only producer of remap rule kind 3.
/// <para>
/// <b>The whole of it is one test, deliberately.</b> A fork is applied whole or refused whole, and the
/// failure it exists to prevent is the rule landing without the row it names: the pages are already migrated
/// by then and the original values are gone, so nothing can detect it and nothing can repair it. Five
/// separate assertions in five separate tests would each pass against a publish that did four of the five.
/// </para>
/// <para>
/// The <c>KEC0041</c> negatives are the preconditions, checked BEFORE the candidate is built, because a fork
/// of a row that is not there cannot be applied at all.
/// </para>
/// </summary>
public class ForkTests
{
    /// <summary>The code every fork precondition failure carries, whichever of the four it is.</summary>
    const string ForkCode = "KEC0041";

    const string SourceKey = "sword";
    const string LegacyKey = "sword_legacy";

    static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    [Fact]
    public async Task AForkKeepsBothRowsLiveAndMovesEveryOlderPageOntoTheCopy()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey(SourceKey), PublishFixtures.Fields(10)));
        await store.PublishAsync(PublishFixtures.Request(0));

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Fork(
                Thing,
                1,
                new ContentKey(SourceKey),
                new ContentKey(LegacyKey),
                PublishFixtures.LegacyField,
                [new ContentFieldEdit(PublishFixtures.ValueField, ContentFieldValue.OfNumber(ContentFieldKind.Int, 25))]));
        ContentPublishResult published = await store.PublishAsync(PublishFixtures.Request(1));

        Assert.Equal(2, published.VersionNumber);
        Assert.Equal(1, published.RulesAppended);

        ContentRowPage page = await store.ListRowsAsync(Thing, 0, null, true, 0, 50);
        ContentRow original = page.Rows.Single(row => row.Id == 1);
        ContentRow copy = page.Rows.Single(row => row.Id != 1);

        // 1. The copy carries a NEW id and the source row's whole field set.
        Assert.Equal(2, copy.Id);
        Assert.Equal(LegacyKey, copy.Key.ToString());
        Assert.Equal(10, copy.Fields[0].Number);
        Assert.Equal(original.Fields.Count, copy.Fields.Count);

        // 2. The copy's flag field is true.
        Assert.Equal(1, copy.Fields[1].Number);
        Assert.False(copy.Fields[1].IsAbsent);

        // 3. The ORIGINAL id is live, under its own key, with the edit's new values and NO flag.
        Assert.Equal(SourceKey, original.Key.ToString());
        Assert.Equal(25, original.Fields[0].Number);
        Assert.True(original.Fields[1].IsAbsent);
        Assert.False(original.IsRetired);
        Assert.False(copy.IsRetired);

        // 4. Exactly one kind 3 rule, naming both ids in that direction.
        RemapRule rule = Assert.Single(store.Rules);
        Assert.Equal(RemapRuleKind.MovedToLegacy, rule.Kind);
        Assert.Equal(1, rule.Sequence);
        Assert.Equal(2, rule.IntroducedIn);
        Assert.Equal(1, rule.FromId);
        Assert.Equal(2, rule.ToId);

        // 5. A page stamped BEFORE the fork resolves onto the copy, which is what keeps what players
        // already rolled pointing at the values it was rolled against. A page stamped after it does not.
        var rules = new RemapRuleSet(store.Rules);
        Assert.True(rules.TryResolve(Thing, 1, 1, out int moved, out _, out _));
        Assert.Equal(2, moved);
        Assert.False(rules.TryResolve(Thing, 1, 2, out _, out _, out _));
    }

    [Fact]
    public async Task TheCopyIsWrittenBeforeTheRuleSoTheRuleNamesADestinationAlreadyLiveAtItsVersion()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        ContentPublisher publisher = PublishFixtures.Publisher(store, registry);

        ContentPublishPlan first = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.Empty,
            ContentEdit.Add(Thing, new ContentKey(SourceKey), PublishFixtures.Fields(10))));

        ContentPublishPlan forked = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.After(first),
            ContentEdit.Fork(
                Thing,
                1,
                new ContentKey(SourceKey),
                new ContentKey(LegacyKey),
                PublishFixtures.LegacyField,
                [])));

        // KEC0017 is what would fire if the rule named a to_id no row of this version carries, and the plan
        // validating is that check passing. Both rows land in one transaction, so there is no window in
        // which the rule exists and the row it names does not.
        RemapRule rule = Assert.Single(forked.AppendedRules);
        Assert.Equal(RemapRuleKind.MovedToLegacy, rule.Kind);
        Assert.Contains(forked.Inserts, insert => insert.Row.Id == rule.ToId);
        Assert.Contains(forked.LiveRows, live => live.Row.Id == rule.ToId && !live.Row.IsRetired);
    }

    [Fact]
    public async Task ForkingASourceRowTheBaseVersionDoesNotCarryIsRefusedByKec0041()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        ContentPublisher publisher = PublishFixtures.Publisher(store, registry);

        ContentPublishPlan plan = await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.Empty,
            ContentEdit.Fork(
                Thing,
                44,
                new ContentKey("missing"),
                new ContentKey(LegacyKey),
                PublishFixtures.LegacyField,
                []));

        Assert.False(plan.IsValid);
        Assert.True(PublishFixtures.Has(plan, ForkCode), PublishFixtures.Findings(plan));
        Assert.Empty(plan.Chunks);
    }

    [Fact]
    public async Task ForkingARowThatIsAlreadyRetiredIsRefusedByKec0041()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        ContentPublisher publisher = PublishFixtures.Publisher(store, registry);

        ContentPublishPlan first = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.Empty,
            ContentEdit.Add(Thing, new ContentKey(SourceKey), PublishFixtures.Fields(10))));
        ContentPublishPlan retired = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.After(first),
            ContentEdit.Retire(Thing, 1, new ContentKey(SourceKey), ContentRetirePolicy.Placeholder, 0)));

        ContentPublishPlan plan = await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.After(retired),
            ContentEdit.Fork(
                Thing,
                1,
                new ContentKey(SourceKey),
                new ContentKey(LegacyKey),
                PublishFixtures.LegacyField,
                []));

        // A fork keeps BOTH rows live, so a retired definition has nothing to keep.
        Assert.False(plan.IsValid);
        Assert.True(PublishFixtures.Has(plan, ForkCode), PublishFixtures.Findings(plan));
    }

    [Theory]
    [InlineData(SourceKey)]
    [InlineData("9legacy")]
    [InlineData("_legacy")]
    [InlineData("legacy_")]
    [InlineData("sword__legacy")]
    [InlineData("Sword_Legacy")]
    public async Task AForkKeyThatIsTakenOrMalformedIsRefusedByKec0041(string forkKey)
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        ContentPublisher publisher = PublishFixtures.Publisher(store, registry);

        ContentPublishPlan first = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.Empty,
            ContentEdit.Add(Thing, new ContentKey(SourceKey), PublishFixtures.Fields(10))));

        ContentPublishPlan plan = await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.After(first),
            ContentEdit.Fork(
                Thing,
                1,
                new ContentKey(SourceKey),
                new ContentKey(forkKey),
                PublishFixtures.LegacyField,
                []));

        Assert.False(plan.IsValid);
        Assert.True(PublishFixtures.Has(plan, ForkCode), PublishFixtures.Findings(plan));
    }

    [Theory]
    [InlineData("not_a_field")]
    [InlineData(PublishFixtures.ValueField)]
    public async Task AFlagFieldTheSchemaDoesNotDeclareAsABoolIsRefusedByKec0041(string flagField)
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        ContentPublisher publisher = PublishFixtures.Publisher(store, registry);

        ContentPublishPlan first = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.Empty,
            ContentEdit.Add(Thing, new ContentKey(SourceKey), PublishFixtures.Fields(10))));

        ContentPublishPlan plan = await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.After(first),
            ContentEdit.Fork(
                Thing,
                1,
                new ContentKey(SourceKey),
                new ContentKey(LegacyKey),
                flagField,
                []));

        // The flag field is the CALLER's and the engine has no opinion about which boolean means superseded
        // on a type it did not define, so it checks only that the field exists and is Bool.
        Assert.False(plan.IsValid);
        Assert.True(PublishFixtures.Has(plan, ForkCode), PublishFixtures.Findings(plan));
    }

    [Fact]
    public async Task TwoForksUnderOneKeyInOneDraftAreRefusedByKec0041()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        ContentPublisher publisher = PublishFixtures.Publisher(store, registry);

        ContentPublishPlan first = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.Empty,
            ContentEdit.Add(Thing, new ContentKey(SourceKey), PublishFixtures.Fields(10)),
            ContentEdit.Add(Thing, new ContentKey("shield"), PublishFixtures.Fields(11))));

        ContentPublishPlan plan = await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.After(first),
            ContentEdit.Fork(
                Thing, 1, new ContentKey(SourceKey), new ContentKey(LegacyKey), PublishFixtures.LegacyField, []),
            ContentEdit.Fork(
                Thing, 2, new ContentKey("shield"), new ContentKey(LegacyKey), PublishFixtures.LegacyField, []));

        // A key a SECOND fork introduces is taken exactly as one an add introduces is. The refusal has to be
        // the precondition rather than a duplicate-key finding on a candidate that should never have been
        // built, so the plan stops before step 2 and carries no chunks.
        Assert.False(plan.IsValid);
        Assert.True(PublishFixtures.Has(plan, ForkCode), PublishFixtures.Findings(plan));
        Assert.Empty(plan.Chunks);
    }

    [Fact]
    public async Task EveryBadForkInOneDraftIsReportedTogetherRatherThanTheFirst()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);
        ContentPublisher publisher = PublishFixtures.Publisher(store, registry);

        ContentPublishPlan first = PublishFixtures.AssertValid(await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.Empty,
            ContentEdit.Add(Thing, new ContentKey(SourceKey), PublishFixtures.Fields(10)),
            ContentEdit.Add(Thing, new ContentKey("shield"), PublishFixtures.Fields(11))));

        ContentPublishPlan plan = await PublishFixtures.PublishAsync(
            store,
            publisher,
            ContentPublishBaseline.After(first),
            ContentEdit.Fork(
                Thing, 1, new ContentKey(SourceKey), new ContentKey("9bad"), PublishFixtures.LegacyField, []),
            ContentEdit.Fork(
                Thing, 2, new ContentKey("shield"), new ContentKey("shield_legacy"), "not_a_field", []));

        Assert.False(plan.IsValid);
        Assert.Equal(
            2,
            plan.Validation.Findings.Count(finding =>
                string.Equals(finding.Code, ForkCode, StringComparison.Ordinal)));
    }
}
