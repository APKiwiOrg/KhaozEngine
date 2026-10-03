using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Store;
using Xunit;
using static KhaozEngine.Tests.Catalog.Publish.TextPublishFixtures;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// Exact-version recovery of text: version N is rebuilt from its OWN values and recorded language mappings
/// after later edits, historical wire spelling is reproduced, and every disagreement or missing proof is a
/// typed refusal before any object or pointer is written. Unknown legacy provenance is accepted as empty only
/// on a read-only proof, and a read never writes provenance.
/// </summary>
public sealed class TextRebuildTests
{
    [Fact]
    public async Task Version_n_rebuilds_its_own_text_after_later_edits_and_a_removal()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        using var rootC = new TemporaryRoot();
        var packA = new FileSystemPackStore(rootA.Path);
        InMemoryContentAuthoringStore store = TextAuthoringFixtures.TextStore(packA);
        await TextAuthoringFixtures.ApplyAsync(store,
            new[] { TextAuthoringFixtures.Add("sword"), TextAuthoringFixtures.Add("shield", 2) },
            Set("sword", "en", "Sword"), Set("sword", "fr", "Epee"), Set("shield", "en", "Shield"));
        await PublishAsync(store);
        await TextAuthoringFixtures.ApplyAsync(store, null, Set("sword", "en", "Blade"));
        await PublishAsync(store);
        await TextAuthoringFixtures.ApplyAsync(store, null, Remove("sword", "fr"));
        await PublishAsync(store);

        var packB = new FileSystemPackStore(rootB.Path);
        ContentPackRebuildResult first = await ContentPackRebuild.RunAsync(store, TextAuthoringFixtures.TextRegistry(), 1, packB);
        Assert.True(first.Rebuilt, first.RefusalDetail);
        ContentVersionRecord one = (await store.GetVersionAsync(1))!;
        IReadOnlyList<ManifestLanguageEntry> v1 = await LanguagesAsync(packB, one);
        Assert.Equal(new[] { "en", "fr" }, v1.Select(language => language.Tag));
        Assert.Equal(Hash("en", (ShieldName, "Shield"), (SwordName, "Sword")), v1[0].TextHash);
        Assert.Equal(Hash("fr", (SwordName, "Epee")), v1[1].TextHash);
        Assert.Equal("Sword", Value(await TextAsync(packB, v1[0].TextHash), SwordName));
        Assert.NotNull(await packB.GetVersionPointerAsync(1));

        var packC = new FileSystemPackStore(rootC.Path);
        ContentPackRebuildResult third = await ContentPackRebuild.RunAsync(store, TextAuthoringFixtures.TextRegistry(), 3, packC);
        Assert.True(third.Rebuilt, third.RefusalDetail);
        IReadOnlyList<ManifestLanguageEntry> v3 = await LanguagesAsync(packC, (await store.GetVersionAsync(3))!);
        Assert.Equal(Hash("en", (ShieldName, "Shield"), (SwordName, "Blade")), v3[0].TextHash);
        Assert.Equal(Hash("fr"), v3[1].TextHash);
        Assert.Equal(0, (await TextAsync(packC, v3[1].TextHash)).EntryCount);
    }

    [Fact]
    public async Task A_historical_en_US_spelling_is_reproduced_byte_for_byte()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        (InMemoryContentAuthoringStore store, TextStoreDouble legacy, string expected) = await HistoricalAsync(rootA, "Sword");
        var packB = new FileSystemPackStore(rootB.Path);

        ContentPackRebuildResult rebuilt = await ContentPackRebuild.RunAsync(legacy, TextAuthoringFixtures.TextRegistry(), 1, packB);

        Assert.True(rebuilt.Rebuilt, rebuilt.RefusalDetail);
        ContentVersionRecord record = (await legacy.GetVersionAsync(1))!;
        ManifestLanguageEntry entry = Assert.Single(await LanguagesAsync(packB, record));
        Assert.Equal("en-US", entry.Tag);
        Assert.Equal(expected, entry.TextHash);
        ContentTextIndex index = await TextAsync(packB, expected);
        Assert.Equal("en-US", index.LanguageTag);
        Assert.Equal("Sword", Value(index, SwordName));
        Assert.Empty(legacy.Calls.Intersect(RowOnlyStoreView.WriteMembers));
        Assert.Equal(1, await store.GetActiveVersionAsync());
    }

    [Theory]
    [InlineData("Blade", ContentPackRebuild.RefusedTextMismatch)]
    [InlineData(null, ContentPackRebuild.RefusedTextMismatch)]
    [InlineData("unmapped", ContentPackRebuild.RefusedServerManifest)]
    public async Task A_disagreeing_value_missing_values_or_a_missing_mapping_refuse_before_any_write(
        string? served, string reason)
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        (InMemoryContentAuthoringStore store, TextStoreDouble legacy, string expected) = await HistoricalAsync(rootA, "Sword");
        string epoch = await store.GetStoreEpochAsync();
        int id = await IdAsync(store, "sword");
        legacy.TextOverride = version => served switch
        {
            null => new ContentVersionTextSnapshot(epoch, version, Array.Empty<ContentTextRevision>(),
                new[] { new ContentTextLanguage("en-us", "en-US", expected) }),
            "unmapped" => new ContentVersionTextSnapshot(epoch, version, Array.Empty<ContentTextRevision>(),
                Array.Empty<ContentTextLanguage>()),
            _ => new ContentVersionTextSnapshot(epoch, version,
                new[] { new ContentTextRevision(TextAuthoringFixtures.Item, id, TextAuthoringFixtures.NameField, "en-us", served, 1, null) },
                new[] { new ContentTextLanguage("en-us", "en-US", expected) }),
        };
        var packB = new FileSystemPackStore(rootB.Path);

        ContentPackRebuildResult refused = await ContentPackRebuild.RunAsync(legacy, TextAuthoringFixtures.TextRegistry(), 1, packB);

        Assert.False(refused.Rebuilt);
        Assert.Equal(reason, refused.RefusalReason);
        Assert.Equal(0, refused.ObjectsWritten);
        Assert.Empty(await ObjectsAsync(packB));
        Assert.Null(await packB.GetVersionPointerAsync(1));
        Assert.Empty(legacy.Calls.Intersect(RowOnlyStoreView.WriteMembers));
    }

    [Fact]
    public async Task Unknown_provenance_is_empty_only_when_both_regenerated_no_language_manifests_match()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        var packA = new FileSystemPackStore(rootA.Path);
        InMemoryContentAuthoringStore store = TextAuthoringFixtures.TextStore(packA);
        await store.ApplyEditsAsync(new[] { TextAuthoringFixtures.Add() }, TextAuthoringFixtures.Actor, TextAuthoringFixtures.Operator, "rows");
        await PublishAsync(store);
        var legacy = new TextStoreDouble(store) { ProvenanceUnknown = true };
        var packB = new FileSystemPackStore(rootB.Path);

        ContentPackRebuildResult rebuilt = await ContentPackRebuild.RunAsync(legacy, TextAuthoringFixtures.TextRegistry(), 1, packB);

        Assert.True(rebuilt.Rebuilt, rebuilt.RefusalDetail);
        Assert.Contains("ReadTextSnapshotAsync", legacy.Calls);
        Assert.Empty(legacy.Calls.Intersect(RowOnlyStoreView.WriteMembers));
        Assert.Empty(await LanguagesAsync(packB, (await store.GetVersionAsync(1))!));
        Assert.NotNull(await packB.GetVersionPointerAsync(1));
    }

    [Fact]
    public async Task Unknown_provenance_over_a_version_that_named_text_refuses_before_any_write()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        var packA = new FileSystemPackStore(rootA.Path);
        InMemoryContentAuthoringStore store = TextAuthoringFixtures.TextStore(packA);
        await TextAuthoringFixtures.ApplyAsync(store, new[] { TextAuthoringFixtures.Add() }, Set("sword", "en", "Sword"));
        await PublishAsync(store);
        var legacy = new TextStoreDouble(store) { ProvenanceUnknown = true };
        var packB = new FileSystemPackStore(rootB.Path);

        ContentPackRebuildResult refused = await ContentPackRebuild.RunAsync(legacy, TextAuthoringFixtures.TextRegistry(), 1, packB);

        Assert.False(refused.Rebuilt);
        Assert.Equal(ContentPackRebuild.RefusedTextProvenance, refused.RefusalReason);
        Assert.Empty(await ObjectsAsync(packB));
        Assert.Null(await packB.GetVersionPointerAsync(1));
        Assert.Empty(legacy.Calls.Intersect(RowOnlyStoreView.WriteMembers));
    }

    [Fact]
    public async Task Verified_stored_manifests_with_empty_lists_are_the_other_proof_and_their_absence_refuses()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        var packA = new FileSystemPackStore(rootA.Path);
        InMemoryContentAuthoringStore store = TextAuthoringFixtures.TextStore(packA);
        await store.ApplyEditsAsync(new[] { TextAuthoringFixtures.Add() }, TextAuthoringFixtures.Actor, TextAuthoringFixtures.Operator, "rows");
        await PublishAsync(store);
        ContentTypeRegistry moved = TextAuthoringFixtures.TextRegistry(ContentVisibility.ServerOnly);
        var packB = new FileSystemPackStore(rootB.Path);

        // The rows no longer reproduce the recorded manifests, and the source's verified manifests prove both
        // language lists empty, so provenance is settled and the refusal is the manifest comparison's. Moving
        // the type to ServerOnly drops it from the client manifest alone, so the client digest differs.
        var proved = new TextStoreDouble(store) { ProvenanceUnknown = true };
        ContentPackRebuildResult mismatch = await ContentPackRebuild.RunAsync(proved, moved, 1, packB);
        Assert.False(mismatch.Rebuilt);
        Assert.Equal(ContentPackRebuild.RefusedClientManifest, mismatch.RefusalReason);

        // With no stored manifest to verify, nothing proves the version text free.
        var blind = new TextStoreDouble(store) { ProvenanceUnknown = true, HidePackStore = true };
        ContentPackRebuildResult unknown = await ContentPackRebuild.RunAsync(blind, moved, 1, packB);
        Assert.False(unknown.Rebuilt);
        Assert.Equal(ContentPackRebuild.RefusedTextProvenance, unknown.RefusalReason);

        Assert.Empty(await ObjectsAsync(packB));
        Assert.Null(await packB.GetVersionPointerAsync(1));
        Assert.Empty(proved.Calls.Concat(blind.Calls).Intersect(RowOnlyStoreView.WriteMembers));
    }

    [Fact]
    public async Task The_sweep_keeps_every_reachable_text_chunk_and_prunes_an_orphan_one()
    {
        using var root = new TemporaryRoot();
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = TextAuthoringFixtures.TextStore(pack);
        await TextAuthoringFixtures.ApplyAsync(store, new[] { TextAuthoringFixtures.Add() }, Set("sword", "en", "Sword"));
        await PublishAsync(store);
        await TextAuthoringFixtures.ApplyAsync(store, null, Set("sword", "en", "Blade"));
        await PublishAsync(store);

        string orphan = Hash("en", ("item.orphan.name", "Nobody"));
        await pack.PutAsync(orphan, ContentTextChunkCodec.Encode("en", Pairs(("item.orphan.name", "Nobody"))));
        await TextAuthoringFixtures.ApplyAsync(store, null, Set("sword", "fr", "Lame"));
        await PublishAsync(store);

        Assert.True(await pack.ExistsAsync(Hash("en", (SwordName, "Sword"))));
        Assert.True(await pack.ExistsAsync(Hash("en", (SwordName, "Blade"))));
        Assert.True(await pack.ExistsAsync(Hash("fr", (SwordName, "Lame"))));
        Assert.False(await pack.ExistsAsync(orphan));
        ContentPackSweepResult swept = await ContentPackSweep.RunValidatedAsync(pack, await store.ListVersionsAsync());
        Assert.True(swept.Ran);
        Assert.Equal(0, swept.Deleted);
    }

    /// <summary>
    /// A version committed row-only through the reference store, and a double that answers it as a LEGACY
    /// version recorded with an <c>en-US</c> text chunk. The recorded manifest hashes are the shipped version's
    /// manifests with that one language entry, digested by the existing manifest text, and the chunk hash is
    /// the existing codec's over the literal key.
    /// </summary>
    static async Task<(InMemoryContentAuthoringStore Store, TextStoreDouble Legacy, string Expected)> HistoricalAsync(
        TemporaryRoot root, string value)
    {
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = TextAuthoringFixtures.TextStore(pack);
        await store.ApplyEditsAsync(new[] { TextAuthoringFixtures.Add() }, TextAuthoringFixtures.Actor, TextAuthoringFixtures.Operator, "rows");
        ContentVersionRecord shipped = await PublishAsync(store);
        string expected = Hash("en-US", (SwordName, value));
        var language = new[] { new ManifestLanguageEntry("en-US", expected) };
        ContentManifest server = (await ManifestAsync(pack, shipped, ContentManifestSide.Server)) with { Languages = language };
        ContentManifest client = (await ManifestAsync(pack, shipped, ContentManifestSide.Client)) with { Languages = language };
        string serverHash = ContentManifestText.Hash(server);
        string clientHash = ContentManifestText.Hash(client);
        string epoch = await store.GetStoreEpochAsync();
        int id = await IdAsync(store, "sword");

        var legacy = new TextStoreDouble(store)
        {
            RecordOverride = record => record.VersionNumber == 1
                ? record with { ServerManifestHash = serverHash, ClientManifestHash = clientHash }
                : record,
            TextOverride = version => new ContentVersionTextSnapshot(
                epoch,
                version,
                new[] { new ContentTextRevision(TextAuthoringFixtures.Item, id, TextAuthoringFixtures.NameField, "en-us", value, 1, null) },
                new[] { new ContentTextLanguage("en-us", "en-US", expected) }),
        };
        return (store, legacy, expected);
    }
}
