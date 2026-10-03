using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Store;
using Xunit;
using static KhaozEngine.Tests.Catalog.Publish.TextPublishFixtures;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// Failure atomicity of text publication: a kill anywhere before the commit leaves the old version and the
/// complete draft, a kill inside the commit leaves only the existing orphan pointer the retry overwrites,
/// an over-cap language body is refused before any object is written, legacy pack writes refuse text they
/// do not represent, and a companion store is committed through the text commit alone.
/// </summary>
public sealed class TextCrashSafetyTests
{
    const string Actor = TextAuthoringFixtures.Actor;
    const string Operator = TextAuthoringFixtures.Operator;

    [Theory]
    [InlineData(ContentPublishStep.BeforeIdAllocation)]
    [InlineData(ContentPublishStep.AfterIdAllocation)]
    [InlineData(ContentPublishStep.BeforeChunkWrite)]
    [InlineData(ContentPublishStep.AfterChunkWrite)]
    [InlineData(ContentPublishStep.BeforeManifestWrite)]
    [InlineData(ContentPublishStep.AfterManifestWrite)]
    [InlineData(ContentPublishStep.BeforeCommit)]
    public async Task A_kill_before_the_commit_leaves_the_old_version_and_the_complete_draft(ContentPublishStep step)
    {
        using var root = new TemporaryRoot();
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = await NamedStoreAsync(pack);
        await TextAuthoringFixtures.ApplyAsync(store, null, Set("sword", "en", "Blade"), Set("sword", "fr", "Lame"));

        ContentPublishCommit killed = Commit(store, pack, TextAuthoringFixtures.TextRegistry(), store, at =>
        {
            if (at == step)
            {
                throw new CrashProbeKill(at);
            }
        });
        CrashProbeKill kill = await Assert.ThrowsAsync<CrashProbeKill>(() => killed.PublishAsync(TextAuthoringFixtures.Request(1)));
        Assert.Equal(step, kill.Step);

        Assert.Equal(1, await store.GetActiveVersionAsync());
        Assert.Null(await store.GetVersionAsync(2));
        Assert.Null(await pack.GetVersionPointerAsync(2));
        ContentDraft held = (await store.GetOpenDraftAsync())!;
        Assert.False(held.IsFrozen);
        Assert.Equal(2, held.TextEditCount);
        Assert.Equal(1, held.LanguageIntroductionCount);
        IContentTextAuthoringStore text = store;
        Assert.Equal("Sword", Assert.Single((await text.ReadTextSnapshotAsync(1)).Revisions).Value);
        bool textWritten = await pack.ExistsAsync(Hash("en", (SwordName, "Blade")));
        Assert.Equal(step == ContentPublishStep.BeforeCommit, textWritten);

        ContentVersionRecord retried = await PublishAsync(store);
        IReadOnlyList<ManifestLanguageEntry> languages = await LanguagesAsync(pack, retried);
        Assert.Equal(Hash("en", (SwordName, "Blade")), languages[0].TextHash);
        Assert.Equal(Hash("fr", (SwordName, "Lame")), languages[1].TextHash);
        Assert.Equal(retried.ServerManifestHash, (await pack.GetVersionPointerAsync(2))!.ServerManifestHash);
    }

    [Fact]
    public async Task A_kill_inside_the_text_commit_leaves_only_the_orphan_pointer_the_retry_overwrites()
    {
        using var root = new TemporaryRoot();
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = await NamedStoreAsync(pack);
        await TextAuthoringFixtures.ApplyAsync(store, null, Set("sword", "en", "Blade"));

        ContentPublishCommit inside = Commit(store, new PointerKillPackStore(pack), TextAuthoringFixtures.TextRegistry(), store);
        await Assert.ThrowsAsync<CrashProbeKill>(() => inside.PublishAsync(TextAuthoringFixtures.Request(1)));

        PackVersionPointer? stale = await pack.GetVersionPointerAsync(2);
        Assert.NotNull(stale);
        Assert.True(await pack.ExistsAsync(stale.ServerManifestHash));
        Assert.True(await pack.ExistsAsync(Hash("en", (SwordName, "Blade"))));
        Assert.Null(await store.GetVersionAsync(2));
        Assert.Equal(1, await store.GetActiveVersionAsync());
        Assert.Equal("Blade", (await store.GetOpenDraftAsync())!.TextState!.Edits.Single().Value);

        await TextAuthoringFixtures.ApplyAsync(store, null, Set("sword", "en", "Edge"));
        ContentVersionRecord retried = await PublishAsync(store);

        PackVersionPointer fresh = (await pack.GetVersionPointerAsync(2))!;
        Assert.NotEqual(stale.ServerManifestHash, fresh.ServerManifestHash);
        Assert.Equal(retried.ServerManifestHash, fresh.ServerManifestHash);
        Assert.Equal(Hash("en", (SwordName, "Edge")), Assert.Single(await LanguagesAsync(pack, retried)).TextHash);
        Assert.True(await pack.ExistsAsync(Hash("en", (SwordName, "Sword"))));
        Assert.False(await pack.ExistsAsync(Hash("en", (SwordName, "Blade"))));
        Assert.False(await pack.ExistsAsync(stale.ServerManifestHash));
    }

    /// <summary>
    /// The 16 MiB uncompressed body bound, crossed by the FRAMING alone: the raw key and value bytes fit under
    /// the cap and the entry-count, key-length and value-length fields push the body over it. The values share
    /// one string instance and the refusal comes before any buffer for the body is allocated, so the case
    /// holds one copy of the data rather than loading 16 MiB repeatedly.
    /// </summary>
    [Fact]
    public async Task A_body_over_16_MiB_by_its_framing_alone_is_refused_before_any_object_is_written()
    {
        using var root = new TemporaryRoot();
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = TextAuthoringFixtures.TextStore(pack);
        string widest = new string('v', ContentTextEdit.MaxValueBytes);
        var rows = new List<ContentEdit>();
        var text = new List<ContentTextEdit>();
        var keys = new List<string>();
        for (int i = 0; i < 1022; i++)
        {
            string key = FormattableString.Invariant($"r{i:D4}");
            rows.Add(TextAuthoringFixtures.Add(key, i + 1));
            text.Add(Set(key, "en", widest));
            keys.Add("item." + key + ".name");
            if (i < 1021)
            {
                text.Add(Set(key, "en", widest, TextAuthoringFixtures.DescriptionField));
                keys.Add("item." + key + ".description");
            }
        }

        long raw = keys.Sum(key => (long)Encoding.UTF8.GetByteCount(key) + ContentTextEdit.MaxValueBytes);
        long framed = raw + 2 + (keys.Count * 3L);
        Assert.True(raw <= ContentPackFormat.MaxChunkUncompressedBytes, "The raw bytes alone fit.");
        Assert.True(framed > ContentPackFormat.MaxChunkUncompressedBytes, "The framed body does not.");

        IContentTextAuthoringStore companion = store;
        await companion.ApplyChangesAsync(new ContentAuthoringChanges(rows, text), Actor, Operator, "wide");
        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.PublishAsync(TextAuthoringFixtures.Request(0)));

        Assert.Equal(ContentAuthoringException.TextBoundsReason, refused.Reason);
        Assert.Empty(await ObjectsAsync(pack));
        Assert.Empty(await store.ListVersionsAsync());
        ContentDraft held = (await store.GetOpenDraftAsync())!;
        Assert.False(held.IsFrozen);
        Assert.Equal(keys.Count, held.TextEditCount);
    }

    [Fact]
    public async Task Legacy_pack_writes_refuse_a_text_plans_row_half_and_an_unwritten_language()
    {
        using var root = new TemporaryRoot();
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = TextAuthoringFixtures.TextStore(pack);
        await TextAuthoringFixtures.ApplyAsync(store, new[] { TextAuthoringFixtures.Add() }, Set("sword", "en-us", "Sword"));
        ContentTextChunkRecord chunk = TextAuthoringFixtures.Chunk("en-us", "Sword");
        (ContentTextPublishSnapshot snapshot, ContentPublishPlan rowPlan) = await TextAuthoringFixtures.FreezeAndPlanAsync(
            store, new[] { new ManifestLanguageEntry("en-us", chunk.Hash) });
        var revision = new ContentTextRevision(
            TextAuthoringFixtures.Item, TextAuthoringFixtures.IdOf(rowPlan, "sword"), TextAuthoringFixtures.NameField, "en-us", "Sword", 1, null);
        var plan = new ContentTextPublishPlan(rowPlan, snapshot, new ContentTextCandidate(
            new[] { revision }, new[] { new ContentTextLanguageDeclaration("en-us", "en-us") },
            Array.Empty<ContentTextRevision>(), new[] { revision }), new[] { chunk });
        ContentPublishCommit legacy = Commit(store, pack, TextAuthoringFixtures.TextRegistry(), store);

        var marked = await Assert.ThrowsAsync<ContentAuthoringException>(() => legacy.WriteAsync(plan.RowPlan));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, marked.Reason);
        var unwritten = await Assert.ThrowsAsync<ContentAuthoringException>(() => legacy.WriteAsync(rowPlan));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, unwritten.Reason);
        Assert.Empty(await ObjectsAsync(pack));
    }

    [Fact]
    public async Task A_companion_store_commits_through_the_text_commit_alone_and_a_row_only_store_keeps_the_old_route()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        var packA = new FileSystemPackStore(rootA.Path);
        InMemoryContentAuthoringStore inner = TextAuthoringFixtures.TextStore(packA);
        var companion = new TextStoreDouble(inner);
        ContentPublishCommit text = Commit(companion, packA, TextAuthoringFixtures.TextRegistry(), inner);

        await companion.ApplyEditsAsync(new[] { TextAuthoringFixtures.Add() }, Actor, Operator, "row only");
        await text.PublishAsync(TextAuthoringFixtures.Request(0));
        await companion.ApplyChangesAsync(TextAuthoringFixtures.Changes(null, Set("sword", "en", "Sword")), Actor, Operator, "text only");
        await text.PublishAsync(TextAuthoringFixtures.Request(1));

        Assert.Equal(2, companion.Calls.Count(call => call == "CommitTextPublishAsync"));
        Assert.Equal(2, companion.Calls.Count(call => call == "FreezeChangesAsync"));
        Assert.DoesNotContain(nameof(IContentAuthoringStore.CommitPublishAsync), companion.Calls);
        Assert.DoesNotContain(nameof(IContentAuthoringStore.FreezeDraftAsync), companion.Calls);
        Assert.Equal("Sword", Assert.Single((await ((IContentTextAuthoringStore)inner).ReadTextSnapshotAsync(2)).Revisions).Value);

        var packB = new FileSystemPackStore(rootB.Path);
        InMemoryContentAuthoringStore plain = TextAuthoringFixtures.TextStore(packB);
        var rowOnly = new RowOnlyStoreView(plain);
        await rowOnly.ApplyEditsAsync(new[] { TextAuthoringFixtures.Add() }, Actor, Operator, "row only");
        ContentPublishResult published = await Commit(rowOnly, packB, TextAuthoringFixtures.TextRegistry(), plain)
            .PublishAsync(TextAuthoringFixtures.Request(0));
        Assert.Equal(1, published.VersionNumber);
        Assert.Contains(nameof(IContentAuthoringStore.FreezeDraftAsync), rowOnly.Calls);
        Assert.Contains(nameof(IContentAuthoringStore.CommitPublishAsync), rowOnly.Calls);
    }

    static async Task<InMemoryContentAuthoringStore> NamedStoreAsync(FileSystemPackStore pack)
    {
        InMemoryContentAuthoringStore store = TextAuthoringFixtures.TextStore(pack);
        await TextAuthoringFixtures.ApplyAsync(store, new[] { TextAuthoringFixtures.Add() }, Set("sword", "en", "Sword"));
        await PublishAsync(store);
        return store;
    }
}
