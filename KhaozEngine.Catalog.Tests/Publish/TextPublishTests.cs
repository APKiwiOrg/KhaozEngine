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
/// Deterministic text publication through the real commit: text-only work, unchanged row and rule bytes,
/// changed-language-only reuse, input-order independence, both manifest language lists, present empty
/// values, the empty index a removed last value leaves, and pending introductions. Every expected chunk
/// is the existing codec's hash over LITERAL derived keys.
/// </summary>
public sealed class TextPublishTests
{
    const string Actor = TextAuthoringFixtures.Actor;
    const string Operator = TextAuthoringFixtures.Operator;

    [Fact]
    public async Task A_text_only_publish_keeps_row_and_rule_bytes_and_both_manifests_name_the_literal_chunk()
    {
        using var root = new TemporaryRoot();
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = TextAuthoringFixtures.TextStore(pack);
        await store.ApplyEditsAsync(new[] { TextAuthoringFixtures.Add() }, Actor, Operator, "rows");
        ContentVersionRecord first = await PublishAsync(store);

        IContentTextAuthoringStore text = store;
        ContentDraft held = await TextAuthoringFixtures.ApplyAsync(text, null, Set("sword", "en", "Sword"));
        Assert.Equal(0, held.EditCount);
        ContentPublishResult result = await store.PublishAsync(TextAuthoringFixtures.Request(1));
        ContentVersionRecord second = (await store.GetVersionAsync(result.VersionNumber))!;

        Assert.Equal(2, second.VersionNumber);
        Assert.Equal(1, result.ChunksWritten);
        foreach (ContentManifestSide side in new[] { ContentManifestSide.Server, ContentManifestSide.Client })
        {
            ContentManifest before = await ManifestAsync(pack, first, side);
            ContentManifest after = await ManifestAsync(pack, second, side);
            Assert.Equal(ChunkHashes(before), ChunkHashes(after));
            Assert.Equal(before.RemapRuleChunkHash, after.RemapRuleChunkHash);
        }

        Assert.Empty(await LanguagesAsync(pack, first));
        ManifestLanguageEntry english = Assert.Single(await LanguagesAsync(pack, second));
        Assert.Equal("en", english.Tag);
        Assert.Equal(Hash("en", (SwordName, "Sword")), english.TextHash);
        Assert.Equal("Sword", Value(await TextAsync(pack, english.TextHash), SwordName));
        Assert.Null(await store.GetOpenDraftAsync());
    }

    [Fact]
    public async Task Hashes_are_deterministic_whatever_order_the_text_arrived_in()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        var packA = new FileSystemPackStore(rootA.Path);
        var packB = new FileSystemPackStore(rootB.Path);
        InMemoryContentAuthoringStore a = TextAuthoringFixtures.TextStore(packA, () => DateTimeOffset.UnixEpoch);
        InMemoryContentAuthoringStore b = TextAuthoringFixtures.TextStore(packB, () => DateTimeOffset.UnixEpoch);
        ContentEdit[] rows = { TextAuthoringFixtures.Add("sword"), TextAuthoringFixtures.Add("shield", 2) };

        await TextAuthoringFixtures.ApplyAsync(a, rows,
            Set("sword", "fr", "Epee"), Set("shield", "en", "Shield"), Set("sword", "en", "Sword"));
        await TextAuthoringFixtures.ApplyAsync(b, rows, Set("sword", "en", "Sword"));
        await TextAuthoringFixtures.ApplyAsync(b, null, Set("shield", "en", "Shield"), Set("sword", "fr", "Epee"));

        ContentVersionRecord left = await PublishAsync(a);
        ContentVersionRecord right = await PublishAsync(b);

        Assert.Equal(left.ServerManifestHash, right.ServerManifestHash);
        Assert.Equal(left.ClientManifestHash, right.ClientManifestHash);
        IReadOnlyList<ManifestLanguageEntry> languages = await LanguagesAsync(packA, left);
        Assert.Equal(new[] { "en", "fr" }, languages.Select(language => language.Tag));
        Assert.Equal(Hash("en", (ShieldName, "Shield"), (SwordName, "Sword")), languages[0].TextHash);
        Assert.Equal(Hash("fr", (SwordName, "Epee")), languages[1].TextHash);
        Assert.Equal(languages, await LanguagesAsync(packB, right));
    }

    [Fact]
    public async Task Only_the_changed_language_is_encoded_and_the_other_keeps_its_hash()
    {
        using var root = new TemporaryRoot();
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = TextAuthoringFixtures.TextStore(pack);
        await TextAuthoringFixtures.ApplyAsync(store, new[] { TextAuthoringFixtures.Add() },
            Set("sword", "en", "Sword"), Set("sword", "fr", "Epee"));
        ContentVersionRecord first = await PublishAsync(store);

        await TextAuthoringFixtures.ApplyAsync(store, null, Set("sword", "fr", "Lame"));
        ContentPublishResult result = await store.PublishAsync(TextAuthoringFixtures.Request(1));
        ContentVersionRecord second = (await store.GetVersionAsync(2))!;

        IReadOnlyList<ManifestLanguageEntry> before = await LanguagesAsync(pack, first);
        IReadOnlyList<ManifestLanguageEntry> after = await LanguagesAsync(pack, second);
        Assert.Equal(before[0], after[0]);
        Assert.Equal(Hash("fr", (SwordName, "Lame")), after[1].TextHash);
        Assert.Equal(1, result.ChunksWritten);
    }

    [Fact]
    public async Task Set_empty_is_a_present_empty_value_and_removing_the_last_value_keeps_an_empty_index()
    {
        using var root = new TemporaryRoot();
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = TextAuthoringFixtures.TextStore(pack);
        await TextAuthoringFixtures.ApplyAsync(store, new[] { TextAuthoringFixtures.Add() }, Set("sword", "en", string.Empty));
        ContentVersionRecord first = await PublishAsync(store);

        ManifestLanguageEntry present = Assert.Single(await LanguagesAsync(pack, first));
        Assert.Equal(Hash("en", (SwordName, string.Empty)), present.TextHash);
        ContentTextIndex index = await TextAsync(pack, present.TextHash);
        var catalog = new ContentStringCatalog(new[] { index }, "en", Shipped);
        Assert.True(catalog.TryGetFromContent(SwordName, out string empty));
        Assert.Equal(string.Empty, empty);

        await TextAuthoringFixtures.ApplyAsync(store, null, Remove("sword", "en"));
        ContentVersionRecord second = await PublishAsync(store);

        ManifestLanguageEntry kept = Assert.Single(await LanguagesAsync(pack, second));
        Assert.Equal("en", kept.Tag);
        Assert.Equal(Hash("en"), kept.TextHash);
        ContentTextIndex emptied = await TextAsync(pack, kept.TextHash);
        Assert.Equal(0, emptied.EntryCount);
        var fallback = new ContentStringCatalog(new[] { emptied }, "en", Shipped);
        Assert.False(fallback.TryGetFromContent(SwordName, out _));
        Assert.Equal("shipped", fallback.Get(SwordName));
    }

    [Fact]
    public async Task A_pending_introduction_survives_set_then_remove_and_publishes_an_empty_language()
    {
        using var root = new TemporaryRoot();
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = TextAuthoringFixtures.TextStore(pack);
        await TextAuthoringFixtures.ApplyAsync(store, new[] { TextAuthoringFixtures.Add() }, Set("sword", "de", "Schwert"));
        await TextAuthoringFixtures.ApplyAsync(store, null, Remove("sword", "de"), Set("sword", "en", "Sword"));
        ContentVersionRecord first = await PublishAsync(store);

        IReadOnlyList<ManifestLanguageEntry> languages = await LanguagesAsync(pack, first);
        Assert.Equal(new[] { "de", "en" }, languages.Select(language => language.Tag));
        Assert.Equal(Hash("de"), languages[0].TextHash);
        Assert.Equal(Hash("en", (SwordName, "Sword")), languages[1].TextHash);
        Assert.Equal(0, (await TextAsync(pack, languages[0].TextHash)).EntryCount);
    }

    [Fact]
    public async Task A_retired_row_keeps_its_values_and_still_takes_text_without_changing_retirement()
    {
        using var root = new TemporaryRoot();
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = TextAuthoringFixtures.TextStore(pack);
        await TextAuthoringFixtures.ApplyAsync(store, new[] { TextAuthoringFixtures.Add() }, Set("sword", "en", "Sword"));
        ContentVersionRecord first = await PublishAsync(store);
        int id = await IdAsync(store, "sword");

        await store.ApplyEditsAsync(
            new[] { ContentEdit.Retire(TextAuthoringFixtures.Item, id, TextAuthoringFixtures.Sword, ContentRetirePolicy.Placeholder, 0) },
            Actor, Operator, "retire");
        ContentPublishResult retired = await store.PublishAsync(TextAuthoringFixtures.Request(1));
        ContentVersionRecord second = (await store.GetVersionAsync(2))!;
        Assert.Equal(await LanguagesAsync(pack, first), await LanguagesAsync(pack, second));
        IContentTextAuthoringStore text = store;
        Assert.Equal("Sword", Assert.Single((await text.ReadTextSnapshotAsync(retired.VersionNumber)).Revisions).Value);

        await TextAuthoringFixtures.ApplyAsync(store, null, Set("sword", "en", "Old sword"));
        ContentVersionRecord third = await PublishAsync(store);
        Assert.Equal(Hash("en", (SwordName, "Old sword")), Assert.Single(await LanguagesAsync(pack, third)).TextHash);
        Assert.True((await store.ListRowsAsync(TextAuthoringFixtures.Item, 0, null, true, 0, 10)).Rows.Single().IsRetired);
    }

    [Fact]
    public async Task A_fork_copies_baseline_values_to_the_final_legacy_id_before_the_original_edit_applies()
    {
        using var root = new TemporaryRoot();
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = TextAuthoringFixtures.TextStore(pack);
        await TextAuthoringFixtures.ApplyAsync(store, new[] { TextAuthoringFixtures.Add() },
            Set("sword", "en", "Sword"), Set("sword", "fr", "Epee"));
        await PublishAsync(store);
        int original = await IdAsync(store, "sword");

        ContentEdit fork = ContentEdit.Fork(
            TextAuthoringFixtures.Item, original, TextAuthoringFixtures.Sword, new ContentKey("old_sword"),
            TextAuthoringFixtures.LegacyField, Array.Empty<ContentFieldEdit>());
        await TextAuthoringFixtures.ApplyAsync(store, new[] { fork },
            Set("sword", "en", "Blade"), Set("old_sword", "fr", "Vieille epee"));
        ContentVersionRecord second = await PublishAsync(store);

        int copy = await IdAsync(store, "old_sword");
        Assert.NotEqual(original, copy);
        IContentTextAuthoringStore text = store;
        Dictionary<(int, string), string> values = (await text.ReadTextSnapshotAsync(2)).Revisions
            .ToDictionary(revision => (revision.DefinitionId, revision.Language), revision => revision.Value);
        Assert.Equal("Blade", values[(original, "en")]);
        Assert.Equal("Epee", values[(original, "fr")]);
        Assert.Equal("Sword", values[(copy, "en")]);
        Assert.Equal("Vieille epee", values[(copy, "fr")]);

        IReadOnlyList<ManifestLanguageEntry> languages = await LanguagesAsync(pack, second);
        Assert.Equal(Hash("en", (OldSwordName, "Sword"), (SwordName, "Blade")), languages[0].TextHash);
        Assert.Equal(Hash("fr", (OldSwordName, "Vieille epee"), (SwordName, "Epee")), languages[1].TextHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_inherited_value_whose_marker_or_type_left_client_visibility_refuses_before_any_write(bool typeLevel)
    {
        using var root = new TemporaryRoot();
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = TextAuthoringFixtures.TextStore(pack);
        await TextAuthoringFixtures.ApplyAsync(store, new[] { TextAuthoringFixtures.Add() }, Set("sword", "en", "Sword"));
        await PublishAsync(store);
        int id = await IdAsync(store, "sword");
        IReadOnlyList<string> objects = await ObjectsAsync(pack);

        await store.ApplyEditsAsync(
            new[] { ContentEdit.Update(TextAuthoringFixtures.Item, id, TextAuthoringFixtures.Sword, new[] { TextAuthoringFixtures.Value(7) }) },
            Actor, Operator, "row only");
        ContentPublishCommit drifted = Commit(store, pack, Drifted(typeLevel), store);

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => drifted.PublishAsync(TextAuthoringFixtures.Request(1)));
        Assert.Equal(ContentAuthoringException.TextTargetIneligibleReason, refused.Reason);
        Assert.Equal(1, await store.GetActiveVersionAsync());
        Assert.Equal(objects, await ObjectsAsync(pack));
        Assert.False((await store.GetOpenDraftAsync())!.IsFrozen);
        IContentTextAuthoringStore text = store;
        Assert.Equal("Sword", Assert.Single((await text.ReadTextSnapshotAsync(1)).Revisions).Value);
    }

    [Fact]
    public async Task Unpaired_surrogates_are_refused_and_a_valid_pair_publishes_its_exact_bytes()
    {
        ContentTextTarget target = TextAuthoringFixtures.Target(TextAuthoringFixtures.NameField, "en");
        Assert.Throws<ArgumentException>(() => ContentTextEdit.Set(target, "a\uD83D"));
        Assert.Throws<ArgumentException>(() => ContentTextEdit.Set(target, "\uDE00b"));
        Assert.Throws<ArgumentException>(() => ContentTextEdit.Set(target, "\uDE00\uD83D"));

        using var root = new TemporaryRoot();
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = TextAuthoringFixtures.TextStore(pack);
        const string astral = "Sword \U0001F5E1";
        await TextAuthoringFixtures.ApplyAsync(store, new[] { TextAuthoringFixtures.Add() }, Set("sword", "en", astral));
        ContentVersionRecord first = await PublishAsync(store);

        ManifestLanguageEntry english = Assert.Single(await LanguagesAsync(pack, first));
        Assert.Equal(Hash("en", (SwordName, astral)), english.TextHash);
        ContentTextIndex index = await TextAsync(pack, english.TextHash);
        Assert.True(index.TryGetUtf8(Encoding.UTF8.GetBytes(SwordName), out ReadOnlySpan<byte> bytes));
        Assert.Equal(Encoding.UTF8.GetBytes(astral), bytes.ToArray());
    }

    [Fact]
    public async Task Multibyte_values_and_derived_keys_are_bounded_in_utf8_bytes_not_characters()
    {
        string widest = new string('€', 2730) + "ab";
        Assert.Equal(ContentTextEdit.MaxValueBytes, Encoding.UTF8.GetByteCount(widest));
        Assert.Throws<ArgumentException>(() => ContentTextEdit.Set(
            TextAuthoringFixtures.Target(TextAuthoringFixtures.NameField, "en"), widest + "c"));

        string field = new string('n', ContentTextKey.MaxKeyLength - "item.".Length - 64 - 1);
        string key = new string('k', 64);
        ContentTypeRegistry registry = WideRegistry(field);
        using var root = new TemporaryRoot();
        var pack = new FileSystemPackStore(root.Path);
        var store = new InMemoryContentAuthoringStore(registry, pack);
        IContentTextAuthoringStore text = store;
        await text.ApplyChangesAsync(
            new ContentAuthoringChanges(
                new[] { ContentEdit.Add(TextAuthoringFixtures.Item, new ContentKey(key), new[] { TextAuthoringFixtures.Value(1) }) },
                new[] { ContentTextEdit.Set(new ContentTextTarget(TextAuthoringFixtures.Item, new ContentKey(key), field, "en"), widest) }),
            Actor, Operator, "wide");
        ContentVersionRecord first = await PublishAsync(store);

        string derived = "item." + key + "." + field;
        Assert.Equal(ContentTextKey.MaxKeyLength, Encoding.UTF8.GetByteCount(derived));
        ManifestLanguageEntry english = Assert.Single(await LanguagesAsync(pack, first));
        Assert.Equal(Hash("en", (derived, widest)), english.TextHash);
        Assert.Equal(widest, Value(await TextAsync(pack, english.TextHash), derived));
    }

    static bool Shipped(string key, out string value)
    {
        value = "shipped";
        return true;
    }

    static string[] ChunkHashes(ContentManifest manifest)
        => manifest.Types.SelectMany(type => type.Chunks.Select(chunk => chunk.Hash)).ToArray();

    static ContentTypeRegistry WideRegistry(string field)
    {
        var schema = new ContentFieldSchema(new ContentFieldEntry[]
        {
            new("value", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new(field, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, false),
        });
        var registry = new ContentTypeRegistry();
        registry.RegisterContentType(
            ContentRegistrationBand.Game, TextAuthoringFixtures.Item.Value, "item", new WideCodec(schema), null, schema,
            ContentVisibility.Client, 256);
        return registry;
    }

    sealed class WideCodec(ContentFieldSchema schema) : ContentRowCodecBase(TextAuthoringFixtures.Item, schema);
}
