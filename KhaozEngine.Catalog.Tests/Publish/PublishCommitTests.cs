using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Store;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// Steps 9, 10 and 11 of spec 6.1: write the pack, commit the one transaction, then sweep.
/// <para>
/// The property every test here is a face of is one sentence. Nothing observable changes until step 10 and
/// step 10 is a single transaction, so a crash at any point leaves either the old version or the new one and
/// never a torn one. The files land first at names nothing references, the pointer lands last of the four,
/// and the active pointer moves as the final statement of the transaction.
/// </para>
/// </summary>
public class PublishCommitTests
{
    static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    [Fact]
    public async Task Step9WritesEveryFileAndThePointerBeforeTheDatabaseTouchesAnything()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);

        var seen = new List<ContentPublishStep>();
        ContentPublishCommit commit = PublishFixtures.Commit(store, pack, registry, seen.Add);
        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)));

        // The hook fires between step 9 and step 10, which is the one moment the files exist and the
        // database does not know about them.
        var atCommit = new List<string>();
        ContentPublishCommit hooked = PublishFixtures.Commit(store, pack, registry, step =>
        {
            if (step != ContentPublishStep.BeforeCommit)
            {
                return;
            }

            atCommit.Add(FormattableString.Invariant($"active={store.GetActiveVersionAsync().Result}"));
            atCommit.Add(FormattableString.Invariant(
                $"pointer={pack.GetVersionPointerAsync(1).Result is not null}"));
        });

        ContentPublishResult published = await hooked.PublishAsync(PublishFixtures.Request(0));

        Assert.Equal(["active=0", "pointer=True"], atCommit);
        Assert.Equal(1, published.VersionNumber);
        Assert.Empty(seen);
        Assert.NotNull(commit);

        PackVersionPointer? pointer = await pack.GetVersionPointerAsync(1);
        Assert.NotNull(pointer);
        Assert.Equal(published.ServerManifestHash, pointer.ServerManifestHash);
        Assert.Equal(published.ClientManifestHash, pointer.ClientManifestHash);
        Assert.True(await pack.ExistsAsync(published.ServerManifestHash));
        Assert.True(await pack.ExistsAsync(published.ClientManifestHash));
    }

    [Fact]
    public async Task EveryChunkHashAndTheRuleChunkAreInTheStoreAfterAPublish()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)));
        await store.PublishAsync(PublishFixtures.Request(0));

        ContentPublishBaseline baseline = await store.ReadPublishBaselineAsync();
        Assert.NotEmpty(baseline.Chunks);
        foreach (ContentChunkRecord chunk in baseline.Chunks)
        {
            Assert.True(await pack.ExistsAsync(chunk.Hash), chunk.Hash);
        }

        Assert.True(await pack.ExistsAsync(ContentRuleChunkCodec.Hash(baseline.Rules)));
    }

    [Fact]
    public async Task AChunkTheStoreAlreadyHoldsIsCheckedForAndNotRewritten()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var counting = new CountingPackStore(new FileSystemPackStore(root.Path));
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, counting);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)),
            ContentEdit.Add(Thing, new ContentKey("far"), PublishFixtures.Fields(2)));
        ContentPublishResult first = await store.PublishAsync(PublishFixtures.Request(0));

        // Republishing the same content writes no chunk at all: every hash is already filed, and a chunk
        // file's name is a hash of its own contents, so the store answers the ExistsAsync and nothing moves.
        ContentPublishBaseline after = await store.ReadPublishBaselineAsync();
        string unchanged = after.Chunks[0].Hash;

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Update(
                Thing,
                1,
                new ContentKey("one"),
                [new ContentFieldEdit(PublishFixtures.ValueField, ContentFieldValue.OfNumber(ContentFieldKind.Int, 99))]));
        ContentPublishResult second = await store.PublishAsync(PublishFixtures.Request(1));

        Assert.Equal(2, second.VersionNumber);
        Assert.Contains(unchanged, counting.ExistsChecks);
        Assert.Equal(1, counting.Puts.Count(hash => string.Equals(hash, unchanged, StringComparison.Ordinal)));
        Assert.NotEqual(first.ServerManifestHash, second.ServerManifestHash);
    }

    [Fact]
    public async Task TheCommitMovesTheActivePointerTogetherWithEveryRowRuleChunkAndAuditEntry()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)),
            ContentEdit.Add(Thing, new ContentKey("two"), PublishFixtures.Fields(2)));
        await store.PublishAsync(PublishFixtures.Request(0));

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Retire(Thing, 2, new ContentKey("two"), ContentRetirePolicy.Placeholder, 0));
        ContentPublishResult published = await store.PublishAsync(PublishFixtures.Request(1));

        Assert.Equal(2, published.VersionNumber);
        Assert.Equal(2, await store.GetActiveVersionAsync());

        ContentVersionRecord? record = await store.GetVersionAsync(2);
        Assert.NotNull(record);
        Assert.Equal(published.ServerManifestHash, record.ServerManifestHash);
        Assert.Equal(published.ClientManifestHash, record.ClientManifestHash);
        Assert.Equal(1, record.BaseVersion);
        Assert.Equal(ContentPackFormat.Generation, record.FormatGeneration);

        // The rows, the rule and the chunk rows all committed with the pointer.
        ContentRowPage page = await store.ListRowsAsync(Thing, 0, null, true, 0, 50);
        Assert.Equal(2, page.Total);
        Assert.True(page.Rows.Single(row => row.Id == 2).IsRetired);

        RemapRule rule = Assert.Single(store.Rules);
        Assert.Equal(1, rule.Sequence);
        Assert.Equal(2, rule.IntroducedIn);
        Assert.Equal(RemapRuleKind.Retired, rule.Kind);
        Assert.Equal(2, rule.FromId);

        ContentPublishBaseline baseline = await store.ReadPublishBaselineAsync();
        Assert.NotEmpty(baseline.Chunks);
        Assert.All(baseline.Chunks, chunk => Assert.True(chunk.IsReused));

        IReadOnlyList<ContentAuditEntry> audit = await store.ListAuditAsync(default, 0, 0, 100);
        ContentAuditEntry entry = audit.First(one =>
            string.Equals(one.Action, ContentAuditActions.Publish, StringComparison.Ordinal));
        Assert.Equal(2, entry.VersionNumber);
        Assert.Equal(PublishFixtures.Actor, entry.Actor);
    }

    [Fact]
    public async Task TheDraftAndItsEditsAreGoneOnceTheCommitLands()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)));
        Assert.NotNull(await store.GetOpenDraftAsync());

        await store.PublishAsync(PublishFixtures.Request(0));

        Assert.Null(await store.GetOpenDraftAsync());
    }

    [Fact]
    public async Task TheCommitConfirmsTheVersionNumberRatherThanTrustingThePlan()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);
        ContentPublisher publisher = PublishFixtures.Publisher(store, registry);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)));
        ContentPublishBaseline baseline = await store.ReadPublishBaselineAsync();
        ContentPublishPlan plan = PublishFixtures.AssertValid(
            await publisher.PrepareAsync(PublishFixtures.Request(0), baseline));

        await store.CommitPublishAsync(plan, PublishFixtures.Request(0));

        // The same plan a second time: its number digested into both manifest hashes at step 8 and the
        // highest published number has moved under it, so the transaction refuses rather than writing a
        // second version 1.
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.CommitPublishAsync(plan, PublishFixtures.Request(0)));

        Assert.Equal(ContentAuthoringException.BaseVersionMovedReason, refused.Reason);
        Assert.Equal(1, await store.GetActiveVersionAsync());
        Assert.Single(await store.ListVersionsAsync());
    }

    [Fact]
    public async Task TheCommitRefusesAPlanTheValidatorDidNotPass()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);
        ContentPublisher publisher = PublishFixtures.Publisher(store, registry);

        // Two rows under one key, which KEC0002 refuses, so the plan carries findings and no manifests.
        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Import(Thing, 1, new ContentKey("same"), PublishFixtures.Fields(1)),
            ContentEdit.Import(Thing, 2, new ContentKey("same"), PublishFixtures.Fields(2)));
        ContentPublishPlan plan = await publisher.PrepareAsync(
            PublishFixtures.Request(0), await store.ReadPublishBaselineAsync());
        Assert.False(plan.IsValid);

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.CommitPublishAsync(plan, PublishFixtures.Request(0)));

        Assert.Equal(ContentAuthoringException.CandidateInvalidReason, refused.Reason);
        Assert.NotEmpty(refused.Findings);
        Assert.Equal(0, await store.GetActiveVersionAsync());
    }

    [Fact]
    public async Task TheActivePointerMovesWhileALoadedSnapshotKeepsServingWhatItRead()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)));
        await store.PublishAsync(PublishFixtures.Request(0));

        // A running server loaded version 1 and holds it.
        ContentSnapshot running = await store.LoadSnapshotAsync(1, registry);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Update(
                Thing,
                1,
                new ContentKey("one"),
                [new ContentFieldEdit(PublishFixtures.ValueField, ContentFieldValue.OfNumber(ContentFieldKind.Int, 77))]));
        await store.PublishAsync(PublishFixtures.Request(1));

        Assert.Equal(2, await store.GetActiveVersionAsync());
        Assert.Equal(1, running.VersionNumber);
        Assert.True(running.TryGetRow(Thing, 1, out ContentRow? held));
        Assert.Equal(1, held.Fields[0].Number);

        ContentSnapshot next = await store.LoadSnapshotAsync(2, registry);
        Assert.Equal(2, next.VersionNumber);
        Assert.True(next.TryGetRow(Thing, 1, out ContentRow? moved));
        Assert.Equal(77, moved.Fields[0].Number);
    }

    [Fact]
    public async Task TheSweepKeepsTheRuleChunkBecauseTheKeepSetIsTheManifestsAndNotTheChunkTable()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);
        ContentPublishCommit commit = PublishFixtures.Commit(store, pack, registry);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)));
        await commit.PublishAsync(PublishFixtures.Request(0));

        // The rule chunk sits at a reserved address outside every type's id space, so no chunk row hangs off
        // it. A keep set read from the chunk table would delete it at the first publish and every later boot
        // would fail closed on an absent chunk, for every version, forever.
        ContentPublishBaseline baseline = await store.ReadPublishBaselineAsync();
        string ruleChunk = ContentRuleChunkCodec.Hash(baseline.Rules);
        Assert.DoesNotContain(baseline.Chunks, chunk => string.Equals(chunk.Hash, ruleChunk, StringComparison.Ordinal));

        ContentPackSweepResult sweep = await commit.SweepAsync();

        Assert.True(sweep.Ran, sweep.SkipReason);
        Assert.Equal(0, sweep.Deleted);
        Assert.True(await pack.ExistsAsync(ruleChunk));
        foreach (ContentChunkRecord chunk in baseline.Chunks)
        {
            Assert.True(await pack.ExistsAsync(chunk.Hash), chunk.Hash);
        }
    }

    [Fact]
    public async Task TheSweepDeletesAnOrphanAndKeepsEveryFileAnyVersionNames()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);
        ContentPublishCommit commit = PublishFixtures.Commit(store, pack, registry);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)));
        await commit.PublishAsync(PublishFixtures.Request(0));

        ContentPublishBaseline first = await store.ReadPublishBaselineAsync();
        string carried = first.Chunks[0].Hash;
        byte[] orphan = ContentRuleChunkCodec.Encode([
            new RemapRule(1, 1, Thing, RemapRuleKind.Retired, 99, 0, [RemapRule.RetirePolicyPlaceholder]),
        ]);
        string orphanHash = ContentRuleChunkCodec.Hash([
            new RemapRule(1, 1, Thing, RemapRuleKind.Retired, 99, 0, [RemapRule.RetirePolicyPlaceholder]),
        ]);
        await pack.PutAsync(orphanHash, orphan);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("two"), PublishFixtures.Fields(2)));
        await commit.PublishAsync(PublishFixtures.Request(1));

        ContentPackSweepResult? sweep = commit.LastSweep;
        Assert.NotNull(sweep);
        Assert.True(sweep.Ran, sweep.SkipReason);
        Assert.Equal(1, sweep.Deleted);
        Assert.False(await pack.ExistsAsync(orphanHash));

        // Version 1's own chunk is still fetchable, because a pinned server and a rollback both need older
        // versions to stay whole.
        Assert.True(await pack.ExistsAsync(carried));
    }

    [Fact]
    public async Task TheSweepIsSkippedWhenAVersionPointerCannotBeRead()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);
        ContentPublishCommit commit = PublishFixtures.Commit(store, pack, registry);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)));
        await commit.PublishAsync(PublishFixtures.Request(0));

        ContentPublishBaseline baseline = await store.ReadPublishBaselineAsync();
        string chunk = baseline.Chunks[0].Hash;
        File.Delete(pack.VersionPointerPathFor(1));

        ContentPackSweepResult sweep = await commit.SweepAsync();

        Assert.False(sweep.Ran);
        Assert.Equal(ContentPackSweep.SkippedListingFailed, sweep.SkipReason);
        Assert.Equal(0, sweep.Deleted);
        Assert.True(await pack.ExistsAsync(chunk));
    }

    [Fact]
    public async Task ASweepAgainstAStoreThatCannotPruneSaysSoRatherThanReportingSuccess()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        var pruneless = new PrunelessPackStore(pack);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pruneless);
        ContentPublishCommit commit = PublishFixtures.Commit(store, pruneless, registry);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)));
        ContentPublishResult published = await commit.PublishAsync(PublishFixtures.Request(0));

        // Deleting nothing and deleting everything are one keystroke apart, so an operator reading a publish
        // response is told which happened.
        Assert.Equal(1, published.VersionNumber);
        ContentPackSweepResult? sweep = commit.LastSweep;
        Assert.NotNull(sweep);
        Assert.False(sweep.Ran);
        Assert.Equal(ContentPackSweep.SkippedNoPruning, sweep.SkipReason);
    }

    [Fact]
    public async Task ThePublishRefusesAPackStoreWithNoPointerHalfBeforeAnyFileIsWritten()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        var pointerless = new PointerlessPackStore(pack);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pointerless);

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(
            () => PublishFixtures.Commit(store, pointerless, registry));

        Assert.Equal(ContentAuthoringException.NoPackStoreReason, refused.Reason);
        Assert.False(Directory.Exists(root.Path) && Directory.GetFiles(root.Path).Length > 0);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AStoreBuiltWithNoPackTargetRefusesToPublishRatherThanCommittingWithoutFiles()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)));

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.PublishAsync(PublishFixtures.Request(0)));

        Assert.Equal(ContentAuthoringException.NoPackStoreReason, refused.Reason);
        Assert.Equal(0, await store.GetActiveVersionAsync());
    }

    [Fact]
    public async Task ThePublishResultCarriesTheChunkCountsAndTheBytesAnOperatorReadsTheBudgetOff()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)),
            ContentEdit.Import(Thing, PublishFixtures.SecondChunkId, new ContentKey("far"), PublishFixtures.Fields(2)));
        ContentPublishResult first = await store.PublishAsync(PublishFixtures.Request(0));

        Assert.Equal(2, first.ChunksWritten);
        Assert.Equal(0, first.ChunksReused);
        Assert.True(first.BytesWritten > 0);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Update(
                Thing,
                1,
                new ContentKey("one"),
                [new ContentFieldEdit(PublishFixtures.ValueField, ContentFieldValue.OfNumber(ContentFieldKind.Int, 5))]));
        ContentPublishResult second = await store.PublishAsync(PublishFixtures.Request(1));

        Assert.Equal(1, second.ChunksWritten);
        Assert.Equal(1, second.ChunksReused);
        Assert.Equal(0, second.RulesAppended);
    }
}
