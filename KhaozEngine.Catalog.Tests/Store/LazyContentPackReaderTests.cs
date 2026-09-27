using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Tests.Catalog.Runtime;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Store;

/// <summary>
/// The explicit lazy reader keeps a caller-sized working set. Snapshot assembly stays on the original
/// constructor, where every chunk read so far remains available until the handoff.
/// </summary>
public sealed class LazyContentPackReaderTests
{
    [Fact]
    public async Task A_sixty_four_chunk_walk_keeps_only_the_bounded_least_recently_used_set()
    {
        using var root = new TemporaryRoot();
        (ContentTypeRegistry registry, ContentManifest manifest, string manifestHash, int[] ids) =
            await BuildPackAsync(root.Path, chunkCount: 64);
        var store = new RecordingPackStore(new FileSystemPackStore(root.Path));
        ContentPackReader reader = ContentPackReader.CreateLazy(
            store, registry, manifest, manifestHash, maxResidentChunks: 4);

        for (int i = 0; i < ids.Length; i++)
        {
            ContentRowRead read = await reader.ReadRowAsync(CatalogPack.TagType, ids[i]);

            Assert.True(read.Success, read.Reason);
            Assert.Equal(ids[i], read.Row!.Id);
            Assert.Equal(Math.Min(i + 1, 4), reader.ChunksRead);
        }

        Assert.Equal(64, store.GetCount);

        // Chunks 60 to 63 are resident. Touch 60, then load 0, so 61 is the least recent and leaves.
        Assert.True((await reader.ReadRowAsync(CatalogPack.TagType, ids[60])).Success);
        Assert.True((await reader.ReadRowAsync(CatalogPack.TagType, ids[0])).Success);
        Assert.True((await reader.ReadRowAsync(CatalogPack.TagType, ids[60])).Success);
        Assert.Equal(65, store.GetCount);
        Assert.Equal(4, reader.ChunksRead);

        Assert.True((await reader.ReadRowAsync(CatalogPack.TagType, ids[61])).Success);
        Assert.Equal(66, store.GetCount);
        Assert.Equal(4, reader.ChunksRead);
    }

    [Fact]
    public async Task Concurrent_lazy_reads_keep_the_loaded_chunk_after_another_read_evicts_it()
    {
        using var root = new TemporaryRoot();
        (ContentTypeRegistry registry, ContentManifest manifest, string manifestHash, int[] ids) =
            await BuildPackAsync(root.Path, chunkCount: 64);
        ContentPackReader reader = ContentPackReader.CreateLazy(
            new FileSystemPackStore(root.Path), registry, manifest, manifestHash, maxResidentChunks: 1);
        var pending = new Task<ContentRowRead>[256];
        for (int i = 0; i < pending.Length; i++)
        {
            pending[i] = reader.ReadRowAsync(CatalogPack.TagType, ids[i % ids.Length]);
        }

        ContentRowRead[] reads = await Task.WhenAll(pending);

        for (int i = 0; i < reads.Length; i++)
        {
            Assert.True(reads[i].Success, reads[i].Reason);
            Assert.Equal(ids[i % ids.Length], reads[i].Row!.Id);
        }

        Assert.Equal(1, reader.ChunksRead);
    }

    [Fact]
    public async Task Lazy_mode_refuses_snapshot_operations_before_fetching()
    {
        using var root = new TemporaryRoot();
        (ContentTypeRegistry registry, ContentManifest manifest, string manifestHash, _) =
            await BuildPackAsync(root.Path, chunkCount: 2);
        var store = new RecordingPackStore(new FileSystemPackStore(root.Path));
        ContentPackReader reader = ContentPackReader.CreateLazy(
            store, registry, manifest, manifestHash, maxResidentChunks: 1);

        Assert.Throws<InvalidOperationException>(() => reader.BuildSnapshot());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAllAsync());
        Assert.Equal(0, store.GetCount);
        Assert.Equal(0, reader.ChunksRead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Lazy_mode_requires_a_positive_resident_chunk_bound(int maxResidentChunks)
    {
        using var root = new TemporaryRoot();
        (ContentTypeRegistry registry, ContentManifest manifest, string manifestHash, _) =
            await BuildPackAsync(root.Path, chunkCount: 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => ContentPackReader.CreateLazy(
            new FileSystemPackStore(root.Path), registry, manifest, manifestHash, maxResidentChunks));
    }

    static async Task<(ContentTypeRegistry Registry, ContentManifest Manifest, string ManifestHash, int[] Ids)>
        BuildPackAsync(string root, int chunkCount)
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        Assert.True(registry.TryGetByKey(
            EngineContentTypes.TagTypeKey, out ContentTypeRegistration? registration));

        var chunks = new List<ManifestChunkEntry>(chunkCount);
        var ids = new int[chunkCount];
        var store = new FileSystemPackStore(root);
        for (int i = 0; i < chunkCount; i++)
        {
            int id = checked(i * registration.ChunkSlots + 1);
            ContentRow row = CatalogSnapshotFixtures.TagRow(
                id, FormattableString.Invariant($"tag_{i}"), i);
            var encoded = ContentChunkCodec.Encode(
                registration,
                i,
                ContentVisibility.Client,
                [
                    new ContentChunkRow(
                        id,
                        false,
                        CatalogSnapshotFixtures.Body(registry, EngineContentTypes.TagTypeKey, row)),
                ]);

            ids[i] = id;
            chunks.Add(new ManifestChunkEntry(
                (uint)encoded.ChunkIndex, (uint)encoded.UncompressedBytes, encoded.Hash));
            await store.PutAsync(encoded.Hash, encoded.StoredFile);
        }

        var manifest = new ContentManifest
        {
            Side = ContentManifestSide.Client,
            VersionNumber = 1,
            FormatGeneration = ContentPackFormat.Generation,
            MinimumServerBuild = 0,
            MinimumClientBuild = 0,
            RemapRuleChunkHash = new string('0', 64),
            Types =
            [
                new ManifestTypeEntry(
                    EngineContentTypes.TagTypeId,
                    EngineContentTypes.TagTypeKey,
                    registration.ChunkSlots,
                    ContentVisibility.Client,
                    chunks),
            ],
            Languages = [],
        };
        return (registry, manifest, ContentManifestText.Hash(manifest), ids);
    }
}
