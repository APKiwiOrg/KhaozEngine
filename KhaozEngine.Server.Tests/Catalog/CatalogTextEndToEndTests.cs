using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// Admin to reader, end to end: text authored through the REAL registered <c>catalog-edit</c> and
/// <c>catalog-publish</c> actions, read back as the client reads it, the verified client manifest off the
/// pack store, the KECT object it names decoded through <see cref="ContentTextIndex"/>, and the derived key
/// resolved through <see cref="ContentStringCatalog"/> over a shipped fallback.
/// <para>
/// Every expectation is a LITERAL derived key and value, so a producer that spelled, sorted or framed a
/// string wrongly cannot agree with itself. Neither the reader nor the fallback is changed for this.
/// </para>
/// </summary>
public sealed class CatalogTextEndToEndTests : IDisposable
{
    /// <summary>The derived key of the sword's name, spelled out rather than derived.</summary>
    const string SwordName = "thing.stone_sword.name";

    /// <summary>A key only the game's shipped catalog holds.</summary>
    const string ShippedOnly = "ui.menu.title";

    readonly CatalogActionHarness _harness = new();

    /// <summary>
    /// An add plus its name in one admin request publishes a manifest naming one English KECT object whose
    /// value resolves through the shipped reader. A text-only update then publishes through the same path
    /// with the row chunks untouched, and the fallback order is unchanged throughout.
    /// </summary>
    [Fact]
    public async Task AdminAddAndName_PublishesAndResolvesThroughTheShippedReader_ThenATextOnlyUpdateDoesToo()
    {
        await EditAsync("""
        {
          "operator": "oid:8f2c",
          "edits": [ { "op": "add", "typeKey": "thing", "key": "stone_sword", "fields": { "value": 4200, "stackable": false } } ],
          "textEdits": [ { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en", "value": "Stone Sword" } ]
        }
        """);
        ContentManifest first = await PublishAsync(0);

        ManifestLanguageEntry english = Assert.Single(first.Languages);
        Assert.Equal("en", english.Tag);
        Assert.Equal(Hash("en", (SwordName, "Stone Sword")), english.TextHash);
        ContentStringCatalog catalog = await CatalogAsync(english);
        Assert.Equal("Stone Sword", catalog.Get(SwordName));
        Assert.Equal("shipped:" + ShippedOnly, catalog.Get(ShippedOnly));
        Assert.Equal("no.such.key", new ContentStringCatalog([await IndexAsync(english)], "en").Get("no.such.key"));

        await EditAsync("""
        { "textEdits": [ { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en", "value": "Stone Blade" } ] }
        """);
        ContentManifest second = await PublishAsync(1);

        ManifestLanguageEntry updated = Assert.Single(second.Languages);
        Assert.Equal(Hash("en", (SwordName, "Stone Blade")), updated.TextHash);
        Assert.Equal("Stone Blade", (await CatalogAsync(updated)).Get(SwordName));
        Assert.Equal(ChunkHashes(first), ChunkHashes(second));
    }

    /// <summary>
    /// <c>Set("")</c> publishes a PRESENT empty value that resolves empty rather than falling back, and a
    /// Remove of the last value keeps English declared with an empty KECT object, so a client still
    /// constructs its default language layer and falls back to the shipped catalog.
    /// </summary>
    [Fact]
    public async Task AnEmptySetResolvesEmpty_AndRemovingTheLastValueKeepsAConstructibleEmptyDefaultLayer()
    {
        await EditAsync("""
        {
          "edits": [ { "op": "add", "typeKey": "thing", "key": "stone_sword", "fields": { "value": 4200, "stackable": false } } ],
          "textEdits": [ { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en", "value": "" } ]
        }
        """);
        ContentManifest first = await PublishAsync(0);

        ManifestLanguageEntry present = Assert.Single(first.Languages);
        Assert.Equal(Hash("en", (SwordName, string.Empty)), present.TextHash);
        ContentStringCatalog empty = await CatalogAsync(present);
        Assert.True(empty.TryGetFromContent(SwordName, out string value));
        Assert.Equal(string.Empty, value);
        Assert.Equal(string.Empty, empty.Get(SwordName));

        await EditAsync("""
        { "textEdits": [ { "op": "remove", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en" } ] }
        """);
        ContentManifest second = await PublishAsync(1);

        ManifestLanguageEntry kept = Assert.Single(second.Languages);
        Assert.Equal("en", kept.Tag);
        Assert.Equal(Hash("en"), kept.TextHash);
        ContentTextIndex index = await IndexAsync(kept);
        Assert.Equal(0, index.EntryCount);
        var layer = new ContentStringCatalog([index], "en", Shipped);
        Assert.Equal(new[] { "en" }, layer.Languages);
        Assert.False(layer.TryGetFromContent(SwordName, out _));
        Assert.Equal("shipped:" + SwordName, layer.Get(SwordName));
    }

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();

    async Task EditAsync(string body) => await _harness.OkAsync("catalog-edit", body);

    /// <summary>
    /// Publishes through the real action and reads the CLIENT manifest it names as a client does, verified
    /// against its hash. The server manifest must carry the same language list.
    /// </summary>
    async Task<ContentManifest> PublishAsync(int expectedBaseVersion)
    {
        JsonElement published = await _harness.OkAsync(
            "catalog-publish", $$"""{ "operator": "oid:8f2c", "expectedBaseVersion": {{expectedBaseVersion}} }""");
        ContentManifestRead client = await ContentPackReader.ReadManifestAsync(
            _harness.Pack,
            published.GetProperty("clientManifestHash").GetString()!,
            ContentManifestSide.Client,
            _harness.Registry);
        Assert.True(client.Success, client.Reason);
        ContentManifestRead server = await ContentPackReader.ReadManifestAsync(
            _harness.Pack,
            published.GetProperty("serverManifestHash").GetString()!,
            ContentManifestSide.Server,
            _harness.Registry);
        Assert.True(server.Success, server.Reason);
        Assert.Equal(server.Manifest!.Languages, client.Manifest!.Languages);
        return client.Manifest!;
    }

    /// <summary>The KECT object one manifest language names, decoded through the shipped reader.</summary>
    async Task<ContentTextIndex> IndexAsync(ManifestLanguageEntry language)
    {
        ReadOnlyMemory<byte>? file = await _harness.Pack.GetAsync(language.TextHash);
        Assert.NotNull(file);
        Assert.True(ContentTextIndex.TryDecode(file.Value.Span, out ContentTextIndex? index, out string? reason), reason);
        Assert.Equal(language.Tag, index!.LanguageTag);
        return index;
    }

    async Task<ContentStringCatalog> CatalogAsync(ManifestLanguageEntry language)
        => new([await IndexAsync(language)], language.Tag, Shipped);

    /// <summary>The game's shipped catalog, which answers every key with a recognizable value.</summary>
    static bool Shipped(string key, out string value)
    {
        value = "shipped:" + key;
        return true;
    }

    /// <summary>The KECT hash the existing codec computes over literal entries.</summary>
    static string Hash(string tag, params (string Key, string Value)[] entries)
        => ContentTextChunkCodec.Hash(
            tag, entries.Select(static entry => new KeyValuePair<string, string>(entry.Key, entry.Value)).ToArray());

    static string[] ChunkHashes(ContentManifest manifest)
        => manifest.Types.SelectMany(static type => type.Chunks.Select(static chunk => chunk.Hash)).ToArray();
}
