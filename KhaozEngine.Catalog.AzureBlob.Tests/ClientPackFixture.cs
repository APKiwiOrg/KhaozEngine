using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.Catalog;

namespace KhaozEngine.Tests.Catalog.AzureBlob;

/// <summary>
/// One published CLIENT version, built from <c>KhaozEngine.Catalog</c> types ALONE: a tag chunk, the remap
/// rule chunk, one text chunk, and the client manifest that names all three.
/// <para>
/// A manifest normally comes from the publisher in <c>KhaozEngine.Catalog.Authoring</c>, which this project
/// must not reference at all: the no-pointer-half fact asserts by interface NAME precisely because that
/// package is absent from the graph. It does not have to be here. <see cref="ContentManifest"/> is an
/// ordinary record, <see cref="ContentManifestCodec.Encode(ContentManifest)"/> writes the file the store
/// holds and <see cref="ContentManifestText.Hash"/> names it, all three shipped in the seam package, so the
/// smallest real closure can be built through the shipped encoders and nothing is hand rolled.
/// </para>
/// <para>
/// It is deliberately NOT a publish: there is no server manifest, no version pointer and no authoring store.
/// What it exists for is one version a fill can copy and a client can then fetch.
/// </para>
/// </summary>
internal sealed class ClientPack
{
    /// <summary>The version number the manifest carries.</summary>
    public const int VersionNumber = 7;

    /// <summary>The language the one text chunk is written for.</summary>
    public const string LanguageTag = "en-US";

    /// <summary>The build ordinal floor the manifest declares, low enough that an ordinary client passes it.</summary>
    public const uint MinimumClientBuild = 12;

    ClientPack(
        ContentTypeRegistry registry,
        IReadOnlyList<KeyValuePair<string, byte[]>> objects,
        string manifestHash)
    {
        Registry = registry;
        Objects = objects;
        Version = new ContentVersionIdentity(VersionNumber, manifestHash);
    }

    /// <summary>The registry the chunks were encoded against, carrying the engine's own types.</summary>
    public ContentTypeRegistry Registry { get; }

    /// <summary>The version's identity, number and CLIENT manifest hash, which is what a fill accepts.</summary>
    public ContentVersionIdentity Version { get; }

    /// <summary>Every object of the closure as (address, bytes), the manifest LAST.</summary>
    public IReadOnlyList<KeyValuePair<string, byte[]>> Objects { get; }

    /// <summary>Builds the version, encoding every file through the shipped encoders.</summary>
    public static ClientPack Build()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        if (!registry.TryGetByKey(EngineContentTypes.TagTypeKey, out ContentTypeRegistration? tag))
        {
            throw new InvalidOperationException("The engine types no longer carry a tag type.");
        }

        EncodedContentChunk tagChunk = ContentChunkCodec.Encode(
            tag,
            0,
            ContentVisibility.Client,
            [TagRow(tag, 1, "metal"), TagRow(tag, 2, "two_handed")]);

        StoredObject rules = CatalogChunks.Rules();
        StoredObject text = CatalogChunks.English();

        var manifest = new ContentManifest
        {
            Side = ContentManifestSide.Client,
            VersionNumber = VersionNumber,
            FormatGeneration = ContentPackFormat.Generation,
            MinimumServerBuild = 11,
            MinimumClientBuild = MinimumClientBuild,
            RemapRuleChunkHash = rules.Hash,
            Types =
            [
                new ManifestTypeEntry(
                    EngineContentTypes.TagTypeId,
                    EngineContentTypes.TagTypeKey,
                    tag.ChunkSlots,
                    ContentVisibility.Client,
                    [new ManifestChunkEntry(0, (uint)tagChunk.UncompressedBytes, tagChunk.Hash)]),
            ],
            Languages = [new ManifestLanguageEntry(LanguageTag, text.Hash)],
        };

        KeyValuePair<string, byte[]>[] objects =
        [
            new(tagChunk.Hash, tagChunk.StoredFile.ToArray()),
            new(rules.Hash, rules.File),
            new(text.Hash, text.File),
            new(ContentManifestText.Hash(manifest), ContentManifestCodec.Encode(manifest)),
        ];

        return new ClientPack(registry, objects, objects[^1].Key);
    }

    /// <summary>Writes every object of the closure into a store, which is what a server's own pack holds.</summary>
    public async Task WriteAsync(IPackStore store)
    {
        for (int i = 0; i < Objects.Count; i++)
        {
            await store.PutAsync(Objects[i].Key, Objects[i].Value);
        }
    }

    static ContentChunkRow TagRow(ContentTypeRegistration tag, int id, string key)
    {
        var row = new ContentRow(
            new ContentTypeId(EngineContentTypes.TagTypeId),
            id,
            new ContentKey(key),
            0,
            false,
            [
                ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, id),
            ]);

        var writer = new ArrayBufferWriter<byte>();
        tag.Codec.Encode(row, writer);
        return new ContentChunkRow(row.Id, row.IsRetired, writer.WrittenSpan.ToArray());
    }
}

/// <summary>A temporary directory that dies with the test, so no test shares a pack root with another.</summary>
internal sealed class TemporaryPackRoot : IDisposable
{
    public TemporaryPackRoot()
        => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "kec-blob-" + Guid.NewGuid().ToString("n"));

    /// <summary>The directory the store is rooted at.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green test over.
        }
    }
}
