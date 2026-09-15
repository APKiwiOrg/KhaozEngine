using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Store;

/// <summary>
/// The ONE reader of spec 9.3, which the server and the client share and differ only in when they call:
/// verify, decompress and decode ONE chunk, and assemble a snapshot out of the chunks that were asked for.
/// <para>
/// The load-bearing fact is the first one. A chunk whose bytes do not hash to the name it was fetched under
/// is refused and NEVER used, because the content address is the whole trust chain (spec 13.2) and a reader
/// that decoded first and checked afterwards would already have acted on bytes nobody signed.
/// </para>
/// </summary>
public class ContentPackReaderTests
{
    [Fact]
    public async Task A_chunk_whose_bytes_do_not_hash_to_its_name_is_refused_and_never_used()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(store);

        // Past the store, which verifies on the way in, so the hostile case is a file that went bad AFTER it
        // was written: a corrupted disk, a poisoned mirror, or a hand-copied tree. The poison here is a
        // perfectly VALID chunk of the same type at the wrong address, which is the shape a mirror serving
        // the wrong object takes and the one a structural check alone would sail straight past.
        await System.IO.File.WriteAllBytesAsync(
            store.PathFor(pack.ItemChunkZero.Hash), pack.ItemChunkOne.StoredFile.ToArray());

        ContentPackReader reader = Reader(store, pack);
        ContentChunkRead read = await reader.ReadChunkAsync(pack.ItemChunkZero.Hash);

        Assert.False(read.Success);
        Assert.Null(read.Chunk);
        Assert.Equal(ContentPackReader.ReasonHashMismatch, read.Reason);
        Assert.Equal(0, reader.ChunksRead);
        Assert.Empty(reader.BuildSnapshot().Rows(CatalogPack.ItemType));
    }

    [Fact]
    public async Task A_chunk_that_decodes_cleanly_produces_rows_the_snapshot_returns_in_ascending_id_order()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(store);

        ContentPackReader reader = Reader(store, pack);

        // Read the SECOND chunk first, so the ascending order below is the snapshot's own and not the order
        // the chunks happened to arrive in.
        Assert.True((await reader.ReadChunkAsync(pack.ItemChunkOne.Hash)).Success);
        Assert.True((await reader.ReadChunkAsync(pack.ItemChunkZero.Hash)).Success);

        IReadOnlyList<ContentRow> rows = reader.BuildSnapshot().Rows(CatalogPack.ItemType);

        Assert.Equal([3, 5, 1030, 1031], rows.Select(r => r.Id));
        Assert.Equal("bronze_sword", rows[0].Key.ToString());
        Assert.True(rows[1].IsRetired);
        Assert.False(rows[0].IsRetired);
        Assert.Equal(2, reader.ChunksRead);
    }

    [Fact]
    public async Task Assembling_a_snapshot_touches_only_the_chunks_a_lookup_asks_for()
    {
        using var root = new TemporaryRoot();
        var backing = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(backing);

        var store = new RecordingPackStore(backing);
        ContentPackReader reader = Reader(store, pack);

        ContentRowRead row = await reader.ReadRowAsync(CatalogPack.ItemType, 1030);

        Assert.True(row.Success);
        Assert.Equal("rune_sword", row.Row!.Key.ToString());
        Assert.Equal([pack.ItemChunkOne.Hash], store.Gets);
        Assert.Equal(1, reader.ChunksRead);

        ContentSnapshot snapshot = reader.BuildSnapshot();
        Assert.Equal([1030, 1031], snapshot.Rows(CatalogPack.ItemType).Select(r => r.Id));
        Assert.Empty(snapshot.Rows(CatalogPack.TagType));

        // A second lookup inside the same chunk is answered from the chunk already decoded.
        Assert.True((await reader.ReadRowAsync(CatalogPack.ItemType, 1031)).Success);
        Assert.Equal(1, store.GetCount);
    }

    [Fact]
    public async Task A_lookup_into_a_chunk_this_version_does_not_carry_is_a_miss_rather_than_a_fetch()
    {
        using var root = new TemporaryRoot();
        var backing = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(backing);

        var store = new RecordingPackStore(backing);
        ContentPackReader reader = Reader(store, pack);

        ContentRowRead missing = await reader.ReadRowAsync(CatalogPack.ItemType, 9_000_000);

        Assert.False(missing.Success);
        Assert.Equal(ContentChunkCodec.ReasonRowMissing, missing.Reason);
        Assert.Equal(0, store.GetCount);

        ContentRowRead gap = await reader.ReadRowAsync(CatalogPack.ItemType, 4);
        Assert.False(gap.Success);
        Assert.Equal(ContentChunkCodec.ReasonRowMissing, gap.Reason);
        Assert.Equal(1, store.GetCount);
    }

    [Fact]
    public async Task A_chunk_absent_from_the_store_reports_the_fetch_outcome_rather_than_throwing()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(store);
        System.IO.File.Delete(store.PathFor(pack.TagChunk.Hash));

        ContentChunkRead read = await Reader(store, pack).ReadChunkAsync(pack.TagChunk.Hash);

        Assert.False(read.Success);
        Assert.Equal(ContentPackReader.ReasonFetchFailed, read.Reason);
        Assert.Equal(pack.TagChunk.Hash, read.Hash);
    }

    [Fact]
    public async Task A_chunk_the_manifest_never_named_is_refused_even_when_it_hashes_correctly()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(store);

        // A valid chunk at a valid address, of a type the manifest carries, at an index it does not. Loading
        // it would mean serving rows no manifest hash covers.
        ContentTypeRegistration tag = Registration(pack, EngineContentTypes.TagTypeKey);
        EncodedContentChunk stray = ContentChunkCodec.Encode(
            tag,
            3,
            ContentVisibility.Client,
            [new ContentChunkRow(12_289, false, Tag(pack, 12_289, "stray"))]);
        await store.PutAsync(stray.Hash, stray.StoredFile);

        ContentChunkRead read = await Reader(store, pack).ReadChunkAsync(stray.Hash);

        Assert.False(read.Success);
        Assert.Equal(ContentManifest.ReasonChunkRangeMismatch, read.Reason);
    }

    [Fact]
    public async Task ReadAllAsync_fetches_and_verifies_every_chunk_the_manifest_names()
    {
        using var root = new TemporaryRoot();
        var backing = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(backing);

        var store = new RecordingPackStore(backing);
        ContentPackRead read = await Reader(store, pack).ReadAllAsync();

        Assert.True(read.Success);
        Assert.Null(read.Reason);
        Assert.NotNull(read.Snapshot);

        // The three chunks, the rule chunk and the text chunk. Boot step 6 verifies every chunk the manifest
        // names, so the text chunk is fetched for its integrity even though the snapshot holds no text.
        Assert.Equal(5, store.GetCount);
        Assert.Contains(pack.RuleChunkHash, store.Gets);
        Assert.Contains(pack.TextChunkHash, store.Gets);

        Assert.Equal([3, 5, 1030, 1031], read.Snapshot.Rows(CatalogPack.ItemType).Select(r => r.Id));
        Assert.Equal([1, 2], read.Snapshot.Rows(CatalogPack.TagType).Select(r => r.Id));
        Assert.Equal(CatalogPack.VersionNumber, read.Snapshot.VersionNumber);
        Assert.Equal(pack.ServerManifestHash, read.Snapshot.Identity.ManifestHash);
        Assert.Single(read.Snapshot.Rules);
        Assert.Equal(5, read.Snapshot.Rules[0].FromId);
        Assert.True(read.Snapshot.IsRetired(CatalogPack.ItemType, 5));
    }

    [Fact]
    public async Task ReadAllAsync_reports_the_first_chunk_it_cannot_verify()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(store);
        System.IO.File.Delete(store.PathFor(pack.RuleChunkHash));

        ContentPackRead read = await Reader(store, pack).ReadAllAsync();

        Assert.False(read.Success);
        Assert.Null(read.Snapshot);
        Assert.Equal(pack.RuleChunkHash, read.Hash);
        Assert.Equal(ContentPackReader.ReasonFetchFailed, read.Reason);
    }

    [Fact]
    public async Task ReadManifestAsync_verifies_the_manifest_against_the_hash_it_was_fetched_under()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(store);

        ContentManifestRead server = await ContentPackReader.ReadManifestAsync(
            store, pack.ServerManifestHash, ContentManifestSide.Server, pack.Registry);

        Assert.True(server.Success);
        Assert.Equal(CatalogPack.VersionNumber, (int)server.Manifest!.VersionNumber);

        // The client manifest of the same version is a different object at a different address, because the
        // sub-domain alone separates the two sides.
        Assert.NotEqual(pack.ServerManifestHash, pack.ClientManifestHash);
        ContentManifestRead wrongSide = await ContentPackReader.ReadManifestAsync(
            store, pack.ClientManifestHash, ContentManifestSide.Server, pack.Registry);
        Assert.False(wrongSide.Success);
        Assert.Equal(ContentManifestCodec.ReasonWrongSide, wrongSide.Reason);

        ContentManifestRead absent = await ContentPackReader.ReadManifestAsync(
            store, new string('c', 64), ContentManifestSide.Server, pack.Registry);
        Assert.False(absent.Success);
        Assert.Equal(ContentPackReader.ReasonFetchFailed, absent.Reason);
    }

    [Fact]
    public async Task A_manifest_that_does_not_hash_to_its_own_name_is_refused()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(store);

        // Same version, a different minimum client build, so the file decodes and its canonical text digests
        // to something the name does not name.
        var forged = pack.ServerManifest with { MinimumClientBuild = 99 };
        await System.IO.File.WriteAllBytesAsync(
            store.PathFor(pack.ServerManifestHash), ContentManifestCodec.Encode(forged));

        ContentManifestRead read = await ContentPackReader.ReadManifestAsync(
            store, pack.ServerManifestHash, ContentManifestSide.Server, pack.Registry);

        Assert.False(read.Success);
        Assert.Null(read.Manifest);
        Assert.Equal(ContentPackReader.ReasonManifestHashMismatch, read.Reason);
    }

    [Fact]
    public void TryVerify_dispatches_on_the_magic_and_refuses_a_kind_it_does_not_know()
    {
        CatalogPack pack = CatalogPack.Build();

        Assert.True(ContentPackReader.TryVerify(pack.TagChunk.StoredFile.Span, pack.TagChunk.Hash, out string? reason));
        Assert.Null(reason);
        Assert.True(ContentPackReader.TryVerify(pack.RuleChunkFile, pack.RuleChunkHash, out _));
        Assert.True(ContentPackReader.TryVerify(pack.TextChunkFile, pack.TextChunkHash, out _));
        Assert.True(ContentPackReader.TryVerify(pack.ServerManifestFile, pack.ServerManifestHash, out _));

        Assert.False(ContentPackReader.TryVerify(pack.TagChunk.StoredFile.Span, pack.ItemChunkZero.Hash, out reason));
        Assert.Equal(ContentPackReader.ReasonHashMismatch, reason);

        Assert.False(ContentPackReader.TryVerify([0x4B, 0x45, 0x43, 0x5A], new string('d', 64), out reason));
        Assert.Equal(ContentPackReader.ReasonHashMismatch, reason);

        Assert.False(ContentPackReader.TryVerify([], new string('d', 64), out reason));
        Assert.Equal(ContentPackReader.ReasonHashMismatch, reason);
    }

    [Fact]
    public async Task The_reader_never_throws_on_bytes_a_store_hands_it()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(store);

        foreach (int cut in new[] { 0, 1, 4, 20, 36 })
        {
            byte[] truncated = pack.ItemChunkZero.StoredFile.Span[..cut].ToArray();
            await System.IO.File.WriteAllBytesAsync(store.PathFor(pack.ItemChunkZero.Hash), truncated);

            ContentChunkRead read = await Reader(store, pack).ReadChunkAsync(pack.ItemChunkZero.Hash);

            Assert.False(read.Success);
            Assert.NotNull(read.Reason);
        }
    }

    static ContentPackReader Reader(IPackStore store, CatalogPack pack)
        => new(store, pack.Registry, pack.ServerManifest, pack.ServerManifestHash);

    static ContentTypeRegistration Registration(CatalogPack pack, string typeKey)
    {
        Assert.True(pack.Registry.TryGetByKey(typeKey, out ContentTypeRegistration? registration));
        return registration;
    }

    static byte[] Tag(CatalogPack pack, int id, string key)
    {
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        Registration(pack, EngineContentTypes.TagTypeKey).Codec.Encode(
            new ContentRow(
                CatalogPack.TagType,
                id,
                new ContentKey(key),
                0,
                false,
                [
                    ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),
                    ContentFieldValue.OfNumber(ContentFieldKind.Int, 1),
                ]),
            writer);
        return writer.WrittenSpan.ToArray();
    }
}
