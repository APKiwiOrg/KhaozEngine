using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// The helpers the text publish, rebuild and crash suites share. Every expectation here is built from
/// LITERAL derived keys through the existing <see cref="ContentTextChunkCodec"/>, never through the
/// producer under test, so a builder that sorted, spelled or framed a chunk wrongly cannot agree with
/// itself.
/// </summary>
internal static class TextPublishFixtures
{
    /// <summary>The literal derived key of the sword's name, <c>item.sword.name</c>.</summary>
    public const string SwordName = "item.sword.name";

    /// <summary>The literal derived key of the shield's name.</summary>
    public const string ShieldName = "item.shield.name";

    /// <summary>The literal derived key of the fork copy's name.</summary>
    public const string OldSwordName = "item.old_sword.name";

    /// <summary>The text chunk hash the existing codec computes over literal entries, in the order given.</summary>
    public static string Hash(string tag, params (string Key, string Value)[] entries)
        => ContentTextChunkCodec.Hash(tag, Pairs(entries));

    /// <summary>The literal entries as the codec takes them.</summary>
    public static KeyValuePair<string, string>[] Pairs(params (string Key, string Value)[] entries)
        => entries.Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Value)).ToArray();

    /// <summary>One side's manifest of one committed version, read back and verified off the pack.</summary>
    public static async Task<ContentManifest> ManifestAsync(
        IPackStore pack, ContentVersionRecord record, ContentManifestSide side)
    {
        string hash = side == ContentManifestSide.Server ? record.ServerManifestHash : record.ClientManifestHash;
        ContentManifestRead read = await ContentPackReader.ReadManifestAsync(pack, hash, side);
        Assert.True(read.Success, read.Reason);
        return read.Manifest!;
    }

    /// <summary>One stored text chunk, decoded through the shipped reader.</summary>
    public static async Task<ContentTextIndex> TextAsync(IPackStore pack, string hash)
    {
        ReadOnlyMemory<byte>? file = await pack.GetAsync(hash);
        Assert.NotNull(file);
        Assert.True(ContentTextIndex.TryDecode(file.Value.Span, out ContentTextIndex? index, out string? reason), reason);
        return index!;
    }

    /// <summary>The value one literal key resolves to in a decoded chunk, or null on a miss.</summary>
    public static string? Value(ContentTextIndex index, string key)
        => index.TryGetUtf8(System.Text.Encoding.UTF8.GetBytes(key), out ReadOnlySpan<byte> value)
            ? System.Text.Encoding.UTF8.GetString(value)
            : null;

    /// <summary>Both manifests' language lists, which must be the same list.</summary>
    public static async Task<IReadOnlyList<ManifestLanguageEntry>> LanguagesAsync(
        IPackStore pack, ContentVersionRecord record)
    {
        ContentManifest server = await ManifestAsync(pack, record, ContentManifestSide.Server);
        ContentManifest client = await ManifestAsync(pack, record, ContentManifestSide.Client);
        Assert.Equal(server.Languages, client.Languages);
        return server.Languages;
    }

    /// <summary>Every object a pack store holds.</summary>
    public static async Task<IReadOnlyList<string>> ObjectsAsync(IPackStorePruning pack)
    {
        var hashes = new List<string>();
        await foreach (string hash in pack.EnumerateAsync())
        {
            hashes.Add(hash);
        }

        hashes.Sort(StringComparer.Ordinal);
        return hashes;
    }

    /// <summary>The definition id of one live row of the text type.</summary>
    public static async Task<int> IdAsync(IContentAuthoringStore store, string key)
        => (await store.ListRowsAsync(TextAuthoringFixtures.Item, 0, null, true, 0, 500))
            .Rows.Single(row => row.Key.Equals(new ContentKey(key))).Id;

    /// <summary>A publish through the store's own commit, which dispatches through the companion.</summary>
    public static async Task<ContentVersionRecord> PublishAsync(InMemoryContentAuthoringStore store)
    {
        int active = await store.GetActiveVersionAsync();
        ContentPublishResult published = await store.PublishAsync(TextAuthoringFixtures.Request(active));
        return (await store.GetVersionAsync(published.VersionNumber))!;
    }

    /// <summary>A Set of one string of the text type.</summary>
    public static ContentTextEdit Set(string key, string language, string value, string field = TextAuthoringFixtures.NameField)
        => ContentTextEdit.Set(TextAuthoringFixtures.Target(field, language, key), value);

    /// <summary>A Remove of one string of the text type.</summary>
    public static ContentTextEdit Remove(string key, string language, string field = TextAuthoringFixtures.NameField)
        => ContentTextEdit.Remove(TextAuthoringFixtures.Target(field, language, key));

    /// <summary>
    /// The text type re-registered with its name marker or the whole type moved to <c>ServerOnly</c>, which is a
    /// registry that no longer lets a shared manifest carry an inherited name.
    /// </summary>
    public static ContentTypeRegistry Drifted(bool typeLevel)
    {
        if (typeLevel)
        {
            return TextAuthoringFixtures.TextRegistry(ContentVisibility.ServerOnly);
        }

        var schema = new ContentFieldSchema(new ContentFieldEntry[]
        {
            new("value", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new(TextAuthoringFixtures.LegacyField, ContentFieldKind.Bool, null, ContentVisibility.Client, false),
            new(TextAuthoringFixtures.NameField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.ServerOnly, false),
            new(TextAuthoringFixtures.DescriptionField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, false),
            new(TextAuthoringFixtures.SecretField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.ServerOnly, false),
        });
        var registry = new ContentTypeRegistry();
        registry.RegisterContentType(
            ContentRegistrationBand.Game,
            TextAuthoringFixtures.Item.Value,
            "item",
            new TextCodec(TextAuthoringFixtures.Item, schema),
            null,
            schema,
            ContentVisibility.Client,
            256);
        return registry;
    }

    /// <summary>A commit half over <paramref name="store"/> whose publisher uses <paramref name="registry"/>.</summary>
    public static ContentPublishCommit Commit(
        IContentAuthoringStore store,
        IPackStore pack,
        ContentTypeRegistry registry,
        IContentIdPersistence ids,
        Action<ContentPublishStep>? onStep = null)
        => new(store, pack, new ContentPublisher(store, ids, registry, onStep));

    sealed class TextCodec(ContentTypeId type, ContentFieldSchema schema) : ContentRowCodecBase(type, schema);
}
