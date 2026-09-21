using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Tests.Catalog.Runtime;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Store;

/// <summary>
/// One published version, built through the shipped encoders, so the store tests and the reader tests read
/// the same bytes. Two types, three chunks, one rule chunk and one text chunk, which is the smallest pack
/// that still has a type spread over more than one chunk: the lazy reader's whole point is that a lookup
/// into chunk 1 never touches chunk 0.
/// </summary>
internal sealed class CatalogPack
{
    CatalogPack(
        ContentTypeRegistry registry,
        IReadOnlyList<EncodedContentChunk> chunks,
        string ruleChunkHash,
        byte[] ruleChunkFile,
        string textChunkHash,
        byte[] textChunkFile,
        ContentManifest serverManifest,
        ContentManifest clientManifest)
    {
        Registry = registry;
        Chunks = chunks;
        RuleChunkHash = ruleChunkHash;
        RuleChunkFile = ruleChunkFile;
        TextChunkHash = textChunkHash;
        TextChunkFile = textChunkFile;
        ServerManifest = serverManifest;
        ClientManifest = clientManifest;
        ServerManifestFile = ContentManifestCodec.Encode(serverManifest);
        ClientManifestFile = ContentManifestCodec.Encode(clientManifest);
        ServerManifestHash = ContentManifestText.Hash(serverManifest);
        ClientManifestHash = ContentManifestText.Hash(clientManifest);
    }

    /// <summary>The version number both manifests carry, and the number the pointer is filed under.</summary>
    public const int VersionNumber = 7;

    /// <summary>The language the one text chunk is written for.</summary>
    public const string LanguageTag = "en-US";

    /// <summary>The registry the chunks were encoded against, carrying every engine type.</summary>
    public ContentTypeRegistry Registry { get; }

    /// <summary>The tag chunk, then the two item chunks, in the order they were encoded.</summary>
    public IReadOnlyList<EncodedContentChunk> Chunks { get; }

    /// <summary>The <c>KECR</c> rule chunk's content address.</summary>
    public string RuleChunkHash { get; }

    /// <summary>The rule chunk as stored.</summary>
    public byte[] RuleChunkFile { get; }

    /// <summary>The <c>KECT</c> text chunk's content address.</summary>
    public string TextChunkHash { get; }

    /// <summary>The text chunk as stored.</summary>
    public byte[] TextChunkFile { get; }

    /// <summary>The server manifest, naming every chunk.</summary>
    public ContentManifest ServerManifest { get; }

    /// <summary>The client manifest, which differs from the server one only by side in this pack.</summary>
    public ContentManifest ClientManifest { get; }

    /// <summary>The server manifest file as stored.</summary>
    public byte[] ServerManifestFile { get; }

    /// <summary>The client manifest file as stored.</summary>
    public byte[] ClientManifestFile { get; }

    /// <summary>The server manifest hash, over the canonical manifest text.</summary>
    public string ServerManifestHash { get; }

    /// <summary>The client manifest hash, which is never equal to the server one.</summary>
    public string ClientManifestHash { get; }

    /// <summary>The tag chunk, covering ids 0 to 4,095.</summary>
    public EncodedContentChunk TagChunk => Chunks[0];

    /// <summary>The first item chunk, covering ids 0 to 1,023.</summary>
    public EncodedContentChunk ItemChunkZero => Chunks[1];

    /// <summary>The second item chunk, covering ids 1,024 to 2,047.</summary>
    public EncodedContentChunk ItemChunkOne => Chunks[2];

    /// <summary>The item type id, which is the engine's own fixed 2.</summary>
    public static ContentTypeId ItemType => new(EngineContentTypes.ItemTypeId);

    /// <summary>The tag type id, the engine's own fixed 1.</summary>
    public static ContentTypeId TagType => new(EngineContentTypes.TagTypeId);

    /// <summary>
    /// Builds the pack, encoding every file through the shipped encoders. The text chunk can be supplied
    /// instead, which is how a caller plants a chunk this reader refuses under a hash that matches it.
    /// </summary>
    /// <param name="textFile">The text chunk as stored, or null for the fixture's own.</param>
    /// <param name="textHash">That chunk's content address, or null for the fixture's own.</param>
    public static CatalogPack Build(byte[]? textFile = null, string? textHash = null)
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);

        ContentTypeRegistration tag = Registration(registry, EngineContentTypes.TagTypeKey);
        ContentTypeRegistration item = Registration(registry, EngineContentTypes.ItemTypeKey);

        EncodedContentChunk tagChunk = ContentChunkCodec.Encode(
            tag,
            0,
            ContentVisibility.Client,
            [
                Row(registry, EngineContentTypes.TagTypeKey, CatalogSnapshotFixtures.TagRow(1, "metal", 10)),
                Row(registry, EngineContentTypes.TagTypeKey, CatalogSnapshotFixtures.TagRow(2, "two_handed", 20)),
            ]);

        EncodedContentChunk itemChunkZero = ContentChunkCodec.Encode(
            item,
            0,
            ContentVisibility.Client,
            [
                Row(registry, EngineContentTypes.ItemTypeKey, CatalogSnapshotFixtures.ItemRow(3, "bronze_sword", false, 1, 100, 0)),
                Row(registry, EngineContentTypes.ItemTypeKey, CatalogSnapshotFixtures.ItemRow(5, "iron_sword", false, 1, 200, 1, isRetired: true)),
            ]);

        EncodedContentChunk itemChunkOne = ContentChunkCodec.Encode(
            item,
            1,
            ContentVisibility.Client,
            [
                Row(registry, EngineContentTypes.ItemTypeKey, CatalogSnapshotFixtures.ItemRow(1030, "rune_sword", false, 1, 300, 2)),
                Row(registry, EngineContentTypes.ItemTypeKey, CatalogSnapshotFixtures.ItemRow(1031, "dragon_sword", false, 1, 400, 3)),
            ]);

        RemapRule[] rules =
        [
            new RemapRule(1, VersionNumber, ItemType, RemapRuleKind.ReplacedBy, 5, 3, default),
        ];
        byte[] ruleFile = ContentRuleChunkCodec.Encode(rules);
        string ruleHash = ContentRuleChunkCodec.Hash(rules);

        KeyValuePair<string, string>[] text =
        [
            new("item.bronze_sword.name", "Bronze sword"),
            new("item.rune_sword.name", "Rune sword"),
        ];
        byte[] file = textFile ?? ContentTextChunkCodec.Encode(LanguageTag, text);
        string hash = textHash ?? ContentTextChunkCodec.Hash(LanguageTag, text);

        ManifestTypeEntry[] types =
        [
            new(
                EngineContentTypes.TagTypeId,
                EngineContentTypes.TagTypeKey,
                tag.ChunkSlots,
                ContentVisibility.Client,
                [Entry(tagChunk)]),
            new(
                EngineContentTypes.ItemTypeId,
                EngineContentTypes.ItemTypeKey,
                item.ChunkSlots,
                ContentVisibility.Client,
                [Entry(itemChunkZero), Entry(itemChunkOne)]),
        ];

        ManifestLanguageEntry[] languages = [new(LanguageTag, hash)];

        return new CatalogPack(
            registry,
            [tagChunk, itemChunkZero, itemChunkOne],
            ruleHash,
            ruleFile,
            hash,
            file,
            Manifest(ContentManifestSide.Server, VersionNumber, ruleHash, types, languages),
            Manifest(ContentManifestSide.Client, VersionNumber, ruleHash, types, languages));
    }

    /// <summary>
    /// The NEXT version of the same pack, with one row of one item chunk edited. Every other chunk address
    /// is unchanged, because a chunk address is the content's, so this is what a client that was refused
    /// again after a publish has to fetch: the new manifest and the one chunk that moved.
    /// </summary>
    public CatalogPack WithEditedItemChunk()
    {
        ContentTypeRegistration item = Registration(Registry, EngineContentTypes.ItemTypeKey);
        EncodedContentChunk edited = ContentChunkCodec.Encode(
            item,
            1,
            ContentVisibility.Client,
            [
                Row(Registry, EngineContentTypes.ItemTypeKey, CatalogSnapshotFixtures.ItemRow(1030, "rune_sword", false, 1, 350, 2)),
                Row(Registry, EngineContentTypes.ItemTypeKey, CatalogSnapshotFixtures.ItemRow(1031, "dragon_sword", false, 1, 400, 3)),
            ]);

        ManifestTypeEntry[] types =
        [
            ClientManifest.Types[0],
            ClientManifest.Types[1] with { Chunks = [Entry(ItemChunkZero), Entry(edited)] },
        ];
        ManifestLanguageEntry[] languages = [new(LanguageTag, TextChunkHash)];

        return new CatalogPack(
            Registry,
            [TagChunk, ItemChunkZero, edited],
            RuleChunkHash,
            RuleChunkFile,
            TextChunkHash,
            TextChunkFile,
            Manifest(ContentManifestSide.Server, VersionNumber + 1, RuleChunkHash, types, languages),
            Manifest(ContentManifestSide.Client, VersionNumber + 1, RuleChunkHash, types, languages));
    }

    /// <summary>Writes every file of the pack, plus the version pointer, into a store.</summary>
    public async Task WriteAsync(FileSystemPackStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        for (int i = 0; i < Chunks.Count; i++)
        {
            await store.PutAsync(Chunks[i].Hash, Chunks[i].StoredFile);
        }

        await store.PutAsync(RuleChunkHash, RuleChunkFile);
        await store.PutAsync(TextChunkHash, TextChunkFile);
        await store.PutAsync(ServerManifestHash, ServerManifestFile);
        await store.PutAsync(ClientManifestHash, ClientManifestFile);
        await store.PutVersionPointerAsync(VersionNumber, ServerManifestHash, ClientManifestHash);
    }

    /// <summary>Every hash the version names, the two manifests included, which is the keep set.</summary>
    public IReadOnlyList<string> EveryHash()
    {
        var hashes = new List<string>
        {
            ServerManifestHash,
            ClientManifestHash,
            RuleChunkHash,
            TextChunkHash,
        };
        for (int i = 0; i < Chunks.Count; i++)
        {
            hashes.Add(Chunks[i].Hash);
        }

        return hashes;
    }

    /// <summary>
    /// Every hash the CLIENT side names, its own manifest included: the client manifest, the rule chunk,
    /// the text chunk and every client-visible chunk. This is what one client cold start has to hold.
    /// </summary>
    public IReadOnlyList<string> EveryClientHash()
    {
        var hashes = new List<string> { ClientManifestHash, RuleChunkHash, TextChunkHash };
        for (int i = 0; i < Chunks.Count; i++)
        {
            hashes.Add(Chunks[i].Hash);
        }

        return hashes;
    }

    /// <summary>Every client-side object as (address, bytes), the manifest first.</summary>
    public IReadOnlyList<KeyValuePair<string, byte[]>> ClientObjects()
    {
        var objects = new List<KeyValuePair<string, byte[]>>
        {
            new(ClientManifestHash, ClientManifestFile),
            new(RuleChunkHash, RuleChunkFile),
            new(TextChunkHash, TextChunkFile),
        };
        for (int i = 0; i < Chunks.Count; i++)
        {
            objects.Add(new KeyValuePair<string, byte[]>(Chunks[i].Hash, Chunks[i].StoredFile.ToArray()));
        }

        return objects;
    }

    static ContentManifest Manifest(
        ContentManifestSide side,
        int versionNumber,
        string ruleHash,
        IReadOnlyList<ManifestTypeEntry> types,
        IReadOnlyList<ManifestLanguageEntry> languages)
        => new()
        {
            Side = side,
            VersionNumber = (uint)versionNumber,
            FormatGeneration = ContentPackFormat.Generation,
            MinimumServerBuild = 11,
            MinimumClientBuild = 12,
            RemapRuleChunkHash = ruleHash,
            Types = types,
            Languages = languages,
        };

    static ManifestChunkEntry Entry(EncodedContentChunk chunk)
        => new((uint)chunk.ChunkIndex, (uint)chunk.UncompressedBytes, chunk.Hash);

    static ContentChunkRow Row(ContentTypeRegistry registry, string typeKey, ContentRow row)
        => new(row.Id, row.IsRetired, CatalogSnapshotFixtures.Body(registry, typeKey, row));

    static ContentTypeRegistration Registration(ContentTypeRegistry registry, string typeKey)
    {
        Assert.True(registry.TryGetByKey(typeKey, out ContentTypeRegistration? registration));
        return registration;
    }
}

/// <summary>
/// A store decorator that counts what was actually fetched, which is how the lazy assembly is pinned: the
/// claim is not that the reader is fast, it is that a lookup into one chunk never asks the store for
/// another.
/// </summary>
internal sealed class RecordingPackStore(IPackStore inner) : IPackStore
{
    readonly ConcurrentQueue<string> _gets = new();

    /// <summary>Every hash <see cref="GetAsync"/> was called with, in call order.</summary>
    public IReadOnlyCollection<string> Gets => _gets;

    /// <summary>How many fetches reached the store behind the decorator.</summary>
    public int GetCount => _gets.Count;

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
        => inner.ExistsAsync(hash, cancellationToken);

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
    {
        _gets.Enqueue(hash);
        return inner.GetAsync(hash, cancellationToken);
    }

    /// <inheritdoc />
    public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        => inner.PutAsync(hash, bytes, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken cancellationToken = default)
        => inner.ListAsync(versionNumber, cancellationToken);
}

/// <summary>A temporary directory that dies with the test, so no test shares a root with another.</summary>
internal sealed class TemporaryRoot : IDisposable
{
    public TemporaryRoot()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "kec-store-" + Guid.NewGuid().ToString("n"));
    }

    /// <summary>The directory the store is rooted at.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Path))
            {
                System.IO.Directory.Delete(Path, recursive: true);
            }
        }
        catch (System.IO.IOException)
        {
            // A leftover temp directory is not worth failing a green test over.
        }
    }
}
