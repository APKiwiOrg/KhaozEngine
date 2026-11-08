using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;

namespace KhaozEngine.Tests.Catalog.Authoring;

public sealed class AuthoringCollectionViewTests
{
    [Fact]
    public void AValidatedBundleCannotAcquireAnUndeclaredLanguageThroughACast()
    {
        var state = new ContentBundleTextState([new("en-us", "en-us")], [new(Name, "Sword")]);
        if (state.Values is ContentBundleTextValue[] writable)
            writable[0] = new ContentBundleTextValue(Target(NameField, "fr"), "Epee");
        var bundle = new ContentBundle(2, "epoch", 0, [], [], [], [], state);
        ContentBundle read = ContentBundleJson.Read(ContentBundleJson.Write(bundle));
        Assert.Equal("en-us", Assert.Single(read.TextState!.Values).Target.Language);
        Protected(state.Languages);
        Protected(state.Values);
    }

    [Fact]
    public void TextSnapshotsAndUpgradePlansExposeOnlyProtectedLists()
    {
        ContentTextEdit edit = ContentTextEdit.Set(Name, "Sword");
        ContentTextLanguageDeclaration declaration = new("en-us", "en-us");
        ContentTextRevision revision = new(Item, 1, NameField, "en-us", "Sword", 1, null);
        var state = new ContentDraftTextState([edit], [declaration]);
        Protected(state.Edits);
        Protected(state.Introductions);
        var snapshot = new ContentVersionTextSnapshot("epoch", 1, [revision], [new("en-us", "en-us", new string('a', 64))]);
        Protected(snapshot.Revisions);
        Protected(snapshot.Languages);
        var candidate = new ContentTextCandidate([revision], [declaration], [revision], [revision]);
        Protected(candidate.Values);
        Protected(candidate.Declarations);
        Protected(candidate.Closes);
        Protected(candidate.Inserts);
        var changes = new ContentAuthoringChanges([Add()], [edit]);
        var upgrade = ContentUpgradePlan.Changes(changes, ["Add sword"]);
        Protected(upgrade.Edits);
        Protected(upgrade.TextEdits);
        Protected(upgrade.ChangeLines);
    }

    [Fact]
    public void AChangeSetViewStaysLiveWithoutExposingItsListOrSyncRoot()
    {
        var changes = new ContentChangeSet();
        IReadOnlyList<ContentEdit> view = changes.Edits;
        Protected(view);
        Assert.Empty(view);
        changes.Apply(Add(value: 1));
        Assert.Equal(new[] { Value(1) }, Assert.Single(view).Fields);
        changes.Apply(Add(value: 2));
        Assert.Equal(new[] { Value(2) }, Assert.Single(view).Fields);
    }

    [Fact]
    public async Task PublicationAndRollbackPlansProtectEveryExposedList()
    {
        var store = TextStore();
        await ApplyAsync(store, [Add()], ContentTextEdit.Set(Name, "Sword"));
        ContentTextChunkRecord chunk = Chunk("en-us", ("sword", "Sword"));
        var (snapshot, rowPlan) = await FreezeAndPlanAsync(store, [new("en-us", chunk.Hash)]);
        int id = Assert.Single(rowPlan.LiveRows).Row.Id;
        ContentTextRevision revision = new(Item, id, NameField, "en-us", "Sword", 1, null);
        var plan = new ContentTextPublishPlan(rowPlan, snapshot,
            new ContentTextCandidate([revision], [new("en-us", "en-us")], [], [revision]), [chunk]);
        Protected(plan.Chunks);
        Protected(plan.Languages);
        Protected(plan.TextCloses);
        Protected(plan.TextInserts);
        Protected(rowPlan.FrozenEdits);
        Protected(rowPlan.Closes);
        Protected(rowPlan.Inserts);
        Protected(rowPlan.LiveRows);
        Protected(rowPlan.AppendedRules);
        Protected(rowPlan.Rules);
        Protected(rowPlan.Chunks);
        Protected(rowPlan.Languages);
        ContentPublishBaseline baseline = ContentPublishBaseline.After(rowPlan);
        Protected(baseline.Rows);
        Protected(baseline.Rules);
        Protected(baseline.Chunks);
        Protected(baseline.Languages);
        var rollback = new ContentRollbackPlan(0, 1, [Add()], [], []);
        Protected(rollback.Edits);
        Protected(rollback.Blockers);
        Protected(rollback.Findings);
        await ((IContentTextAuthoringStore)store).CommitTextPublishAsync(plan, Request(0), null);
        ContentRebuildSnapshot rebuild = await ContentRebuildSnapshot.ReadAsync(store, TextRegistry(), 1);
        Protected(rebuild.Rows);
        Protected(rebuild.Rules);
        Protected(rebuild.Languages);
        Assert.Equal("sword", Assert.Single(rebuild.Rows).Key.ToString());
    }

    static void Protected<T>(IReadOnlyList<T> values)
    {
        Assert.False(values is IList<T>);
        Assert.False(values is ICollection);
    }
}
