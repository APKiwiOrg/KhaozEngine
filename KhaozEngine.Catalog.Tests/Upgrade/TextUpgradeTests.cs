using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// Content upgrades that author TEXT: text-only work previewed and applied as real work, later planners
/// seeing earlier text, sticky declarations deciding what a plan introduces, and the proofs that keep another
/// operator's translation from being adopted or discarded.
/// </summary>
public sealed class TextUpgradeTests : IDisposable
{
    const string FrenchId = "name-in-french";
    const string GermanId = "name-in-german";

    /// <summary>The fragment of the refusal a store without the text companion gives a plan carrying text.</summary>
    const string ContentUpgradeTextReason = "no text authoring companion";

    readonly TemporaryCatalogDatabase _files = new();

    [Fact]
    public async Task A_text_only_upgrade_previews_its_text_and_applies_as_real_work()
    {
        InMemoryContentAuthoringStore store = await SeedAsync();
        var set = new ContentUpgradeSet(SetsName(FrenchId, 1, "fr", "Epee"));
        CatalogFootprint before = await UpgradeFixtures.FootprintAsync(store);

        ContentUpgradeReport preview = await ContentUpgradeRunner.RunAsync(store, TextRegistry(), set, UpgradeFixtures.Preview());

        Assert.Equal(ContentUpgradeOutcome.PreviewOnly, preview.Outcome);
        ContentUpgradeStepResult planned = Assert.Single(preview.Steps);
        Assert.Equal(ContentUpgradeStepState.WouldPublish, planned.State);
        Assert.Contains("text set type 1024 'sword' name [fr]: \"Epee\"", planned.ChangeLines);
        Assert.Contains("language fr introduced", planned.ChangeLines);
        Assert.Equal(before, await UpgradeFixtures.FootprintAsync(store));

        ContentUpgradeReport applied = await ContentUpgradeRunner.RunAsync(store, TextRegistry(), set, UpgradeFixtures.Apply(1));

        Assert.Equal(ContentUpgradeOutcome.Applied, applied.Outcome);
        Assert.Equal(2, Assert.Single(applied.Steps).PublishedVersion);
        ContentVersionTextSnapshot text = await ((IContentTextAuthoringStore)store).ReadTextSnapshotAsync(2);
        Assert.Equal(new[] { "en", "fr" }, text.Languages.Select(language => language.WireTag));
        Assert.Equal("Epee", text.Revisions.Single(revision => revision.Language == "fr").Value);
        ContentUpgradeRecord record = Assert.Single(await store.ListUpgradesAsync());
        Assert.Equal((FrenchId, ContentUpgradeDisposition.Applied, 2), (record.Id, record.Disposition, record.VersionNumber));
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Contains(
            await store.ListAuditAsync(default, 0, 0, 500),
            entry => entry.Action == ContentAuditActions.Publish && entry.LanguageTag == "fr" && entry.VersionNumber == 2);

        ContentUpgradeReport again = await ContentUpgradeRunner.RunAsync(store, TextRegistry(), set, UpgradeFixtures.Apply());
        Assert.Equal(ContentUpgradeOutcome.UpToDate, again.Outcome);
    }

    [Fact]
    public async Task A_later_planner_sees_the_text_an_earlier_definition_published()
    {
        InMemoryContentAuthoringStore store = await SeedAsync();
        ContentBundleTextState? seen = null;
        int seenFormat = 0;
        var german = new ContentUpgradeDefinition(GermanId, 2, "names in german", context =>
        {
            seen = context.BaselineText;
            seenFormat = context.Baseline.FormatVersion;
            ContentBundleTextValue? french = context.BaselineText.Values.FirstOrDefault(value => value.Target.Language == "fr");
            return french is null
                ? ContentUpgradePlan.Refused("the french name is not in the baseline.")
                : ContentUpgradePlan.Changes(
                    new ContentAuthoringChanges([], [ContentTextEdit.Set(Target(NameField, "de"), french.Value + " (de)")]),
                    ["name in german"]);
        });
        var set = new ContentUpgradeSet(SetsName(FrenchId, 1, "fr", "Epee"), german);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(store, TextRegistry(), set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Equal(ContentBundle.TextFormatVersion, seenFormat);
        Assert.Contains(seen!.Values, value => value.Target.Language == "fr" && value.Value == "Epee");
        ContentVersionTextSnapshot text = await ((IContentTextAuthoringStore)store).ReadTextSnapshotAsync(3);
        Assert.Equal("Epee (de)", text.Revisions.Single(revision => revision.Language == "de").Value);
        Assert.Equal(new[] { 2, 3 }, (await store.ListUpgradesAsync()).Select(record => record.VersionNumber));
    }

    [Fact]
    public async Task A_sticky_declaration_decides_what_a_plan_introduces()
    {
        InMemoryContentAuthoringStore store = await SeedAsync();
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "fr"), "Epee"));
        await ApplyAsync(text, null, ContentTextEdit.Remove(Target(NameField, "fr")));
        await store.PublishAsync(Request(1));
        var set = new ContentUpgradeSet(SetsName(FrenchId, 1, "fr", "Lame"));

        ContentUpgradeReport preview = await ContentUpgradeRunner.RunAsync(store, TextRegistry(), set, UpgradeFixtures.Preview());
        Assert.DoesNotContain("language fr introduced", Assert.Single(preview.Steps).ChangeLines);
        ContentUpgradeReport applied = await ContentUpgradeRunner.RunAsync(store, TextRegistry(), set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Applied, applied.Outcome);
        Assert.Equal("Lame", (await text.ReadTextSnapshotAsync(3)).Revisions.Single(r => r.Language == "fr").Value);

        ContentTextEdit french = ContentTextEdit.Set(Target(NameField, "fr"), "Lame");
        var changes = new ContentAuthoringChanges([], [french]);
        var declaresFrench = new ContentBundleTextState([new ContentTextLanguageDeclaration("fr", "fr")], []);
        var declaresNothing = new ContentBundleTextState([], []);
        ContentDraft plain = Draft([french], []);
        ContentDraft introducing = Draft([french], [ContentTextLanguageDeclaration.Introduce("fr")]);
        Assert.True(ContentUpgradeDraftMatch.IsPlan(plain, changes, declaresFrench));
        Assert.False(ContentUpgradeDraftMatch.IsPlan(introducing, changes, declaresFrench));
        Assert.True(ContentUpgradeDraftMatch.IsPlan(introducing, changes, declaresNothing));
        Assert.False(ContentUpgradeDraftMatch.IsPlan(plain, changes, declaresNothing));

        // A rival's Set replaced by a Remove keeps its introduction, so the draft holds work nobody planned.
        ContentDraft rival = Draft(
            [french, ContentTextEdit.Remove(Target(DescriptionField, "it"))],
            [ContentTextLanguageDeclaration.Introduce("it")]);
        Assert.False(ContentUpgradeDraftMatch.IsPlan(rival, changes, declaresFrench));
        Assert.False(ContentUpgradeDraftMatch.IsKnownWork(rival, [], [french]));
        Assert.False(ContentUpgradeDraftMatch.IsKnownWork(Draft([french], [ContentTextLanguageDeclaration.Introduce("it")]), [], [french]));
        Assert.True(ContentUpgradeDraftMatch.IsKnownWork(introducing, [], [french]));
        Assert.True(ContentUpgradeDraftMatch.HoldsKnownWork(rival, [], [french]));
        Assert.False(ContentUpgradeDraftMatch.IsPlan(Draft([ContentTextEdit.Remove(Target(NameField, "fr"))], []), changes, declaresFrench));
    }

    [Fact]
    public async Task Backend_held_rival_text_invisible_to_an_old_wrapper_is_neither_published_nor_discarded()
    {
        InMemoryContentAuthoringStore store = await SeedAsync();
        var wrapper = new OldWrapperStore(store)
        {
            AfterNextWrite = () => store.ApplyChangesAsync(
                new ContentAuthoringChanges([], [ContentTextEdit.Set(Target(DescriptionField, "en"), "Rival")]),
                "operator",
                "oid:operator",
                string.Empty),
        };
        var adds = new ContentUpgradeDefinition("adds-shield", 1, "adds a shield", _ => ContentUpgradePlan.Changes(
            [ContentEdit.Add(Item, new ContentKey("shield"), [TextAuthoringFixtures.Value(4)])], ["adds shield"]));

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            wrapper, TextRegistry(), new ContentUpgradeSet(adds), UpgradeFixtures.Apply());

        Assert.NotEqual(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Equal(1, await store.GetActiveVersionAsync());
        Assert.Empty(await store.ListUpgradesAsync());
        ContentDraft held = (await store.GetOpenDraftAsync())!;
        Assert.Equal("Rival", held.TextState!.Edits.Single().Value);
        Assert.Equal(1, held.EditCount);
        Assert.False(held.IsFrozen);
        Assert.DoesNotContain(
            await store.ListAuditAsync(default, 0, 0, 500), entry => entry.Action == ContentAuditActions.DraftDiscard);
    }

    [Fact]
    public async Task A_rival_translation_landing_before_the_atomic_discard_is_kept()
    {
        InMemoryContentAuthoringStore inner = await SeedAsync();
        var store = new TextUpgradeStore(inner)
        {
            FailNextPublish = new ContentAuthoringException(
                "the injected publish refusal.", default, 0, ContentAuthoringException.CandidateInvalidReason),
        };
        store.BeforeTryDiscard = () => inner.ApplyChangesAsync(
            new ContentAuthoringChanges([], [ContentTextEdit.Set(Target(DescriptionField, "fr"), "Rival")]),
            "operator",
            "oid:operator",
            string.Empty);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            store, TextRegistry(), new ContentUpgradeSet(SetsName(FrenchId, 1, "fr", "Epee")), UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Failed, report.Outcome);
        Assert.Equal((1, 0), store.Discards);
        ContentDraft held = (await inner.GetOpenDraftAsync())!;
        Assert.Equal(
            new[] { ("name", "Epee"), ("description", "Rival") },
            held.TextState!.Edits.Select(edit => (edit.Target.FieldName, edit.Value!)));
        Assert.Equal(1, await inner.GetActiveVersionAsync());
        Assert.DoesNotContain(
            await inner.ListAuditAsync(default, 0, 0, 500), entry => entry.Action == ContentAuditActions.DraftDiscard);
    }

    [Fact]
    public async Task A_run_s_own_failed_text_draft_is_cleared_through_the_atomic_discard()
    {
        InMemoryContentAuthoringStore inner = await SeedAsync();
        var store = new TextUpgradeStore(inner)
        {
            FailNextPublish = new ContentAuthoringException(
                "the injected publish refusal.", default, 0, ContentAuthoringException.CandidateInvalidReason),
        };

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            store, TextRegistry(), new ContentUpgradeSet(SetsName(FrenchId, 1, "fr", "Epee")), UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Failed, report.Outcome);
        Assert.Equal((1, 1), store.Discards);
        Assert.Null(await inner.GetOpenDraftAsync());
        Assert.Equal(1, await inner.GetActiveVersionAsync());
    }

    [Fact]
    public async Task An_unknown_or_hidden_baseline_text_refuses_the_upgrade_before_anything_is_written()
    {
        InMemoryContentAuthoringStore inner = await SeedAsync();
        var set = new ContentUpgradeSet(SetsName(FrenchId, 1, "fr", "Epee"));
        CatalogFootprint before = await UpgradeFixtures.FootprintAsync(inner);

        var unknown = new TextUpgradeStore(inner) { ProvenanceUnknown = true };
        ContentUpgradeReport refused = await ContentUpgradeRunner.RunAsync(unknown, TextRegistry(), set, UpgradeFixtures.Apply());
        Assert.Equal(ContentUpgradeOutcome.Failed, refused.Outcome);
        Assert.Contains(refused.Diagnostics, diagnostic => diagnostic.Message.Contains("no complete text record", StringComparison.Ordinal));
        Assert.Equal(before, await UpgradeFixtures.FootprintAsync(inner));

        var hidden = new TextUpgradeStore(inner) { RebuildExportAsFormatOne = true };
        ContentUpgradeReport stripped = await ContentUpgradeRunner.RunAsync(hidden, TextRegistry(), set, UpgradeFixtures.Apply());
        Assert.Equal(ContentUpgradeOutcome.Failed, stripped.Outcome);
        Assert.Contains(stripped.Diagnostics, diagnostic => diagnostic.Message.Contains("cannot see", StringComparison.Ordinal));
        Assert.Equal(before, await UpgradeFixtures.FootprintAsync(inner));
        Assert.Null(await inner.GetOpenDraftAsync());
    }

    [Fact]
    public async Task A_text_plan_is_refused_by_a_store_without_the_companion_and_old_proofs_claim_nothing()
    {
        InMemoryContentAuthoringStore store = await SeedAsync();
        CatalogFootprint before = await UpgradeFixtures.FootprintAsync(store);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            new OldWrapperStore(store), TextRegistry(), new ContentUpgradeSet(SetsName(FrenchId, 1, "fr", "Epee")), UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Refused, report.Outcome);
        Assert.Contains(ContentUpgradeTextReason, Assert.Single(report.Steps).Reason, StringComparison.Ordinal);
        Assert.Equal(before, await UpgradeFixtures.FootprintAsync(store));

        var incomplete = new ContentDraft(1, UpgradeFixtures.Actor, DateTimeOffset.UnixEpoch, string.Empty, new ContentChangeSet());
        Assert.False(ContentUpgradeDraftMatch.IsKnownWork(incomplete, []));
        Assert.False(ContentUpgradeDraftMatch.IsKnownWork(incomplete, [], []));
        Assert.True(ContentUpgradeDraftMatch.IsKnownWork(Draft([], []), []));
        Assert.Throws<ArgumentException>(() => ContentUpgradePlan.Changes(new ContentAuthoringChanges([], []), []));
        Assert.Throws<ArgumentException>(() => ContentUpgradePlan.Changes(Array.Empty<ContentEdit>(), []));
    }

    /// <inheritdoc />
    public void Dispose() => _files.Dispose();

    /// <summary>Version 1: one row named in English, published through the companion.</summary>
    async Task<InMemoryContentAuthoringStore> SeedAsync()
    {
        var store = new InMemoryContentAuthoringStore(TextRegistry(), _files.Pack());
        await ApplyAsync(store, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        await store.PublishAsync(Request(0));
        return store;
    }

    /// <summary>A definition that names the sword in one language, satisfied once the baseline already does.</summary>
    static ContentUpgradeDefinition SetsName(string id, int order, string language, string value)
        => new(id, order, "names the sword in " + language, context =>
            context.BaselineText.Values.Any(held => held.Target.Equals(Target(NameField, language)) && held.Value == value)
                ? ContentUpgradePlan.AlreadySatisfied("the name is already there.")
                : ContentUpgradePlan.Changes(
                    new ContentAuthoringChanges([], [ContentTextEdit.Set(Target(NameField, language), value)]),
                    ["names the sword in " + language]));

    static ContentDraft Draft(IReadOnlyList<ContentTextEdit> text, IReadOnlyList<ContentTextLanguageDeclaration> introductions)
        => new(
            new ContentDraftTextState(text, introductions),
            1,
            UpgradeFixtures.Actor,
            DateTimeOffset.UnixEpoch,
            ContentUpgradeRunner.NoteFor(FrenchId),
            new ContentChangeSet());
}
