using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Authoring;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// The text plan proves its candidate's live values are EXACTLY the expected set: the baseline's, plus each
/// fork copy of its source's baseline values at the copy's final id, plus every frozen Set and Remove. A
/// candidate that applies every intent and also drops, omits or changes anything else is refused, and the
/// idempotent intents (a Set equal to the baseline, a Remove of an absent value) are accepted and commit.
/// </summary>
public sealed class ExactValueTextPublishTests
{
    static readonly ContentTextRevision[] None = Array.Empty<ContentTextRevision>();
    static readonly ContentTextLanguageDeclaration[] English = { new("en", "en") };

    [Fact]
    public async Task A_candidate_that_also_closes_an_unnamed_string_is_refused()
    {
        (InMemoryContentAuthoringStore store, ContentTextRevision sword, ContentTextRevision shield) = await TwoNamedRowsAsync();
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, new[] { Reprice(sword) }, ContentTextEdit.Set(Target(NameField, "en"), "Blade"));
        ContentTextChunkRecord chunk = Chunk("en", "Blade");
        (ContentTextPublishSnapshot snapshot, ContentPublishPlan rowPlan) = await FreezeAndPlanAsync(
            store, new[] { new ManifestLanguageEntry("en", chunk.Hash) });
        var blade = new ContentTextRevision(Item, sword.DefinitionId, NameField, "en", "Blade", 3, null);

        Assert.Throws<ArgumentException>(() => new ContentTextPublishPlan(rowPlan, snapshot,
            new ContentTextCandidate(new[] { blade }, English, new[] { sword, shield }, new[] { blade }), new[] { chunk }));

        var plan = new ContentTextPublishPlan(rowPlan, snapshot,
            new ContentTextCandidate(new[] { shield, blade }, English, new[] { sword }, new[] { blade }), new[] { chunk });
        await text.CommitTextPublishAsync(plan, Request(2), null);
        Assert.Equal(new[] { "Blade", "Shield" }, (await text.ReadTextSnapshotAsync(3)).Revisions.Select(r => r.Value).OrderBy(v => v, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_candidate_that_changes_a_string_no_intent_names_is_refused()
    {
        (InMemoryContentAuthoringStore store, ContentTextRevision sword, ContentTextRevision shield) = await TwoNamedRowsAsync();
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, new[] { Reprice(sword) }, ContentTextEdit.Set(Target(NameField, "en"), "Blade"));
        ContentTextChunkRecord chunk = Chunk("en", "Blade");
        (ContentTextPublishSnapshot snapshot, ContentPublishPlan rowPlan) = await FreezeAndPlanAsync(
            store, new[] { new ManifestLanguageEntry("en", chunk.Hash) });
        var blade = new ContentTextRevision(Item, sword.DefinitionId, NameField, "en", "Blade", 3, null);
        var buckler = new ContentTextRevision(Item, shield.DefinitionId, NameField, "en", "Buckler", 3, null);

        Assert.Throws<ArgumentException>(() => new ContentTextPublishPlan(rowPlan, snapshot,
            new ContentTextCandidate(new[] { blade, buckler }, English, new[] { sword, shield }, new[] { blade, buckler }),
            new[] { chunk }));
        Assert.Null(await store.GetVersionAsync(3));
    }

    [Fact]
    public async Task A_candidate_that_omits_a_fork_copy_of_a_baseline_value_is_refused()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        await PublishNamedRowAsync(store, "sword", "Sword");
        ContentTextRevision sword = (await text.ReadTextSnapshotAsync(1)).Revisions.Single();
        ContentEdit fork = ContentEdit.Fork(
            Item, sword.DefinitionId, Sword, new ContentKey("old_sword"), LegacyField, Array.Empty<ContentFieldEdit>());
        await store.ApplyEditsAsync(new[] { fork }, Actor, Operator, "fork");
        ContentTextChunkRecord chunk = Chunk("en", "Sword twice");
        (ContentTextPublishSnapshot snapshot, ContentPublishPlan rowPlan) = await FreezeAndPlanAsync(
            store, new[] { new ManifestLanguageEntry("en", chunk.Hash) });

        Assert.Throws<ArgumentException>(() => new ContentTextPublishPlan(rowPlan, snapshot,
            new ContentTextCandidate(new[] { sword }, English, None, None), new[] { chunk }));

        var copied = new ContentTextRevision(Item, IdOf(rowPlan, "old_sword"), NameField, "en", "Sword", 2, null);
        var plan = new ContentTextPublishPlan(rowPlan, snapshot,
            new ContentTextCandidate(new[] { sword, copied }, English, None, new[] { copied }), new[] { chunk });
        await text.CommitTextPublishAsync(plan, Request(1), null);
        Assert.Equal(2, (await text.ReadTextSnapshotAsync(2)).Revisions.Count);
    }

    [Fact]
    public async Task A_set_equal_to_the_baseline_value_is_accepted_and_commits_without_a_new_revision()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        await PublishNamedRowAsync(store, "sword", "Sword");
        ContentTextRevision sword = (await text.ReadTextSnapshotAsync(1)).Revisions.Single();
        ContentDraft held = await ApplyAsync(text, new[] { Reprice(sword) }, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        Assert.Equal(1, held.TextEditCount);
        ContentTextChunkRecord chunk = Chunk("en", "Sword");
        (ContentTextPublishSnapshot snapshot, ContentPublishPlan rowPlan) = await FreezeAndPlanAsync(
            store, new[] { new ManifestLanguageEntry("en", chunk.Hash) });

        var plan = new ContentTextPublishPlan(rowPlan, snapshot,
            new ContentTextCandidate(new[] { sword }, English, None, None), new[] { chunk });
        await text.CommitTextPublishAsync(plan, Request(1), null);

        ContentTextRevision kept = Assert.Single((await text.ReadTextSnapshotAsync(2)).Revisions);
        Assert.Equal(sword, kept);
        Assert.Null(await store.GetOpenDraftAsync());
    }

    [Fact]
    public async Task A_remove_of_an_absent_value_in_a_declared_language_is_accepted_and_commits()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        await PublishNamedRowAsync(store, "sword", "Sword");
        ContentTextRevision sword = (await text.ReadTextSnapshotAsync(1)).Revisions.Single();
        await ApplyAsync(text, new[] { Add("shield", 2) }, ContentTextEdit.Set(Target(NameField, "en", "shield"), "Shield"));
        ContentDraft held = await ApplyAsync(text, null, ContentTextEdit.Remove(Target(NameField, "en", "shield")));
        Assert.Equal(ContentTextEditOperation.Remove, held.TextState!.Edits.Single().Operation);
        ContentTextChunkRecord chunk = Chunk("en", "Sword");
        (ContentTextPublishSnapshot snapshot, ContentPublishPlan rowPlan) = await FreezeAndPlanAsync(
            store, new[] { new ManifestLanguageEntry("en", chunk.Hash) });

        var plan = new ContentTextPublishPlan(rowPlan, snapshot,
            new ContentTextCandidate(new[] { sword }, English, None, None), new[] { chunk });
        await text.CommitTextPublishAsync(plan, Request(1), null);

        Assert.Equal(sword, Assert.Single((await text.ReadTextSnapshotAsync(2)).Revisions));
        Assert.Equal(2, (await store.ListRowsAsync(Item, 0, null, true, 0, 10)).Total);
    }

    static ContentEdit Reprice(ContentTextRevision row)
        => ContentEdit.Update(Item, row.DefinitionId, Sword, new[] { Value(9) });

    static async Task<(InMemoryContentAuthoringStore Store, ContentTextRevision Sword, ContentTextRevision Shield)> TwoNamedRowsAsync()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        await PublishNamedRowAsync(store, "sword", "Sword");
        await PublishNamedRowAsync(store, "shield", "Shield");
        ContentVersionTextSnapshot held = await text.ReadTextSnapshotAsync(2);
        return (store, held.Revisions.Single(r => r.Value == "Sword"), held.Revisions.Single(r => r.Value == "Shield"));
    }
}
