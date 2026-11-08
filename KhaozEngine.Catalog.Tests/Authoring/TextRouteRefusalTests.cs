using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;

namespace KhaozEngine.Tests.Catalog.Authoring;

public sealed class TextRouteRefusalTests
{
    [Theory]
    [InlineData("type", ContentAuthoringRefusal.TypeUnknown)]
    [InlineData("field", ContentAuthoringRefusal.TextTargetFieldUnknown)]
    [InlineData("type-visibility", ContentAuthoringRefusal.TextTargetIneligible)]
    [InlineData("field-visibility", ContentAuthoringRefusal.TextTargetIneligible)]
    [InlineData("kind", ContentAuthoringRefusal.TextTargetIneligible)]
    public void TargetEligibilityNamesTheFailedDeclarationAndAcceptsItsRepair(string defect, ContentAuthoringRefusal reason)
    {
        ContentTypeRegistry registry = TextRegistry(defect == "type-visibility"
            ? ContentVisibility.ServerOnly : ContentVisibility.Client);
        string field = defect switch { "field" => "absent", "field-visibility" => SecretField, "kind" => "value", _ => NameField };
        var target = new ContentTextTarget(defect == "type" ? new ContentTypeId(1025) : Item, Sword, field, "en");

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() =>
            ContentTextTargetEligibility.Require(registry, target));

        Assert.Equal(reason, refused.Refusal);
        Assert.Equal(target.Type, refused.Type);
        Assert.Equal(0, refused.Id);
        if (defect != "type")
        {
            Assert.Equal("item", refused.TypeKey);
            Assert.Equal(field, refused.FieldName);
            Assert.Equal("en", refused.Language);
        }
        Assert.Equal(NameField, ContentTextTargetEligibility.Require(TextRegistry(), Target(NameField, "en")).Name);
    }

    [Fact]
    public async Task DerivedKeyEligibilityReportsTheInclusiveByteLimit()
    {
        string field = new('n', 64);
        var schema = new ContentFieldSchema([
            new("value", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new(field, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, false)]);
        var registry = new ContentTypeRegistry();
        registry.RegisterContentType(ContentRegistrationBand.Game, Item.Value, new string('t', 64),
            new MarkerCodec(schema), null, schema, ContentVisibility.Client, 256);
        var target = Target(field, "en", new string('k', 63));
        var store = new InMemoryContentAuthoringStore(registry);

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            ApplyAsync(store, [Add(new string('k', 63))], ContentTextEdit.Set(target, "Sword")));

        Assert.Equal(ContentAuthoringRefusal.TextKeyBytesExceeded, refused.Refusal);
        Assert.Equal(Item, refused.Type);
        Assert.Equal(0, refused.Id);
        Assert.Equal(field, refused.FieldName);
        Assert.Equal("en", refused.Language);
        Assert.Equal(193, refused.ActualBytes);
        Assert.Equal(192, refused.MaximumBytes);
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Empty(await store.ListAuditAsync(default, 0, 0, 500));
        ContentDraft atLimit = await ApplyAsync(store, [Add(new string('k', 62))],
            ContentTextEdit.Set(Target(field, "en", new string('k', 62)), "Sword"));
        Assert.Equal(new string('k', 62), Assert.Single(atLimit.TextState!.Edits).Target.Key.ToString());
        ContentDraft belowLimit = await ApplyAsync(store, [Add(new string('k', 61))],
            ContentTextEdit.Set(Target(field, "en", new string('k', 61)), "Shield"));
        Assert.Equal(2, belowLimit.TextEditCount);
        Assert.Equal(new string('k', 61), belowLimit.TextState!.Edits[1].Target.Key.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RowOnlyBundlesDistinguishCarriedTextFromAnImpliedSection(bool carriesText)
    {
        ContentBundle bundle = carriesText
            ? new(2, "epoch", 0, [], [], [], [], new ContentBundleTextState([], []))
            : new(2, "epoch", 0, [], [], [], []);

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() =>
            ContentTextCompatibility.RequireRowOnlyBundle(bundle, "import"));

        Assert.Equal(carriesText ? ContentAuthoringRefusal.TextBundleOnRowOnlyRoute
            : ContentAuthoringRefusal.TextFormatOnRowOnlyRoute, refused.Refusal);
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, refused.Reason);
        Assert.Equal("import", refused.MemberName);
        if (!carriesText) Assert.Equal(2, refused.ActualFormatVersion);
        ContentTextCompatibility.RequireRowOnlyBundle(new(1, "epoch", 0, [], [], [], []), "import");
    }

    [Fact]
    public void MissingBundleTextIsReportedBeforeTheCompanionReadsIt()
    {
        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() =>
            ContentBundleTextCompatibility.TextOf(new(2, "epoch", 0, [], [], [], []), "import"));

        Assert.Equal(ContentAuthoringRefusal.BundleTextSectionMissing, refused.Refusal);
        Assert.Equal(2, refused.ActualFormatVersion);
        Assert.Empty(ContentBundleTextCompatibility.TextOf(
            new(2, "epoch", 0, [], [], [], [], new ContentBundleTextState([], [])), "import").Values);
    }

    [Theory]
    [InlineData("row")]
    [InlineData("text")]
    [InlineData("language")]
    public void PendingWorkReportsEveryCountAndAnEmptyDraftAllowsImport(string work)
    {
        var draft = new ContentDraft(new ContentDraftTextState(
            work == "text" ? [ContentTextEdit.Set(Target(NameField, "en"), "Sword")] : [],
            work == "language" ? [new("en", "en")] : []),
            0, Actor, DateTimeOffset.UnixEpoch, "pending", new ContentChangeSet(work == "row" ? [Add()] : []));

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() =>
            ContentBundleTextCompatibility.RequireNoPendingWork(draft, "import"));

        Assert.Equal(ContentAuthoringRefusal.DraftOpen, refused.Refusal);
        Assert.Equal(ContentAuthoringException.DraftOpenReason, refused.Reason);
        Assert.Equal("import", refused.MemberName);
        Assert.Equal(work == "row" ? 1 : 0, refused.RowEditCount);
        Assert.Equal(work == "text" ? 1 : 0, refused.TextEditCount);
        Assert.Equal(work == "language" ? 1 : 0, refused.LanguageIntroductionCount);
        ContentBundleTextCompatibility.RequireNoPendingWork(new(ContentDraftTextState.Empty,
            0, Actor, DateTimeOffset.UnixEpoch, "empty", new ContentChangeSet()), "import");
    }

    [Theory]
    [InlineData("text")]
    [InlineData("language")]
    public void RowOnlyDraftRefusalReportsTheTextItWouldLose(string work)
    {
        var draft = new ContentDraft(new ContentDraftTextState(
            work == "text" ? [ContentTextEdit.Set(Target(NameField, "en"), "Sword")] : [],
            work == "language" ? [new("en", "en")] : []),
            0, Actor, DateTimeOffset.UnixEpoch, "pending", new ContentChangeSet());

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() =>
            ContentTextCompatibility.RequireNoHeldText(draft, "discard"));

        Assert.Equal(ContentAuthoringRefusal.TextDraftOnRowOnlyRoute, refused.Refusal);
        Assert.Equal("discard", refused.MemberName);
        Assert.Equal(work == "text" ? 1 : 0, refused.TextEditCount);
        Assert.Equal(work == "language" ? 1 : 0, refused.LanguageIntroductionCount);
        ContentTextCompatibility.RequireNoHeldText(new(ContentDraftTextState.Empty,
            0, Actor, DateTimeOffset.UnixEpoch, "empty", new ContentChangeSet()), "discard");
    }

    [Fact]
    public async Task ARowOnlyCommitNamesTheTextPlanAndAcceptsTheUnmarkedRowPlan()
    {
        var store = TextStore();
        await ApplyAsync(store, [Add()], ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        var (_, plan) = await FreezeAndPlanAsync(store, []);
        ContentPublishPlan marked = plan.WithFrozenText(new([], []));

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() =>
            ContentTextCompatibility.RequireRowOnlyPlan(marked, "commit"));

        Assert.Equal(ContentAuthoringRefusal.TextPlanOnRowOnlyRoute, refused.Refusal);
        Assert.Equal("commit", refused.MemberName);
        Assert.Equal(1, refused.VersionNumber);
        ContentTextCompatibility.RequireRowOnlyPlan(plan, "commit");
    }

    [Fact]
    public void ExportNamesTheTextRevisionWhoseLiveRowIsMissing()
    {
        var revision = new ContentTextRevision(Item, 3, NameField, "en", "Sword", 1, null);
        var snapshot = new ContentVersionTextSnapshot("epoch", 1, [revision], [new("en", "en", "a")]);

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() =>
            ContentBundleTextCompatibility.Export("epoch", 1, [], [], [], [], snapshot));

        Assert.Equal(ContentAuthoringRefusal.TextExportRowUnknown, refused.Refusal);
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, refused.Reason);
        Assert.Equal(default, refused.Type);
        Assert.Equal(0, refused.Id);
        Assert.Equal(Item, refused.TextRowType);
        Assert.Equal(3, refused.TextRowId);
        Assert.Equal(NameField, refused.FieldName);
        Assert.Equal("en", refused.Language);
        Assert.Equal(1, refused.VersionNumber);
        Assert.Equal("ExportBundleAsync", refused.MemberName);
        ContentBundle control = ContentBundleTextCompatibility.Export("epoch", 1, [],
            [new(Item, 3, Sword, false, null, [])], [], [], snapshot);
        Assert.Equal("Sword", Assert.Single(control.TextState!.Values).Value);
    }

    sealed class MarkerCodec(ContentFieldSchema schema) : ContentRowCodecBase(Item, schema);
}
