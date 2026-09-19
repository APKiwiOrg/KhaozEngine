using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Store;

/// <summary>
/// The content-addressed store of spec 8.1 and 8.2: one file per hash under a two-level shard, written temp
/// then moved, with the version pointer of publish step 9 outside the shard tree.
/// <para>
/// The two facts worth more than the round trip are pinned here. <c>PutAsync</c> VERIFIES the digest of the
/// bytes it was handed, because a store that will write anything under any name is not content addressed and
/// every later verify-on-read is then meaningless. And <c>ListAsync</c> answers from the version POINTER
/// rather than from the directory, because a store's job is to say what a version contains and not what
/// happens to be on disk.
/// </para>
/// </summary>
public class FileSystemPackStoreTests
{
    [Fact]
    public async Task GetAsync_returns_null_for_an_absent_hash_rather_than_throwing()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);

        ReadOnlyMemory<byte>? absent = await store.GetAsync(new string('a', 64));

        Assert.Null(absent);
        Assert.False(await store.ExistsAsync(new string('a', 64)));
    }

    [Fact]
    public async Task GetAsync_answers_null_for_a_name_that_is_not_a_content_address()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);

        Assert.Null(await store.GetAsync("../../etc/passwd"));
        Assert.False(await store.ExistsAsync("../../etc/passwd"));
    }

    [Fact]
    public async Task PutAsync_lands_the_file_in_the_two_level_shard_and_leaves_no_temporary()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();

        await store.PutAsync(pack.TagChunk.Hash, pack.TagChunk.StoredFile);

        string hash = pack.TagChunk.Hash;
        string expected = Path.Combine(root.Path, hash[..2], hash.Substring(2, 2), hash + ".kec");
        Assert.Equal(expected, store.PathFor(hash));
        Assert.True(File.Exists(expected));
        Assert.Empty(Directory.GetFiles(root.Path, "*.tmp", SearchOption.AllDirectories));

        ReadOnlyMemory<byte>? read = await store.GetAsync(hash);
        Assert.NotNull(read);
        Assert.True(pack.TagChunk.StoredFile.Span.SequenceEqual(read.Value.Span));
    }

    // The one statement of the key layout, which three providers derive a name from: the local store's path,
    // the HTTP store's URI and the blob store's key. A change here moves all three together, which is what
    // lets a client read through HttpPackStore what another provider wrote.
    [Fact]
    public void RelativeKeyFor_is_the_two_shard_segments_then_the_hash_and_the_extension()
    {
        string hash = new string('a', 62) + "9f";

        string key = FileSystemPackStore.RelativeKeyFor(hash);

        Assert.Equal("aa/aa/" + hash + FileSystemPackStore.FileExtension, key);
        Assert.Equal(hash[..2], key[..2]);
        Assert.Equal(hash.Substring(2, 2), key.Substring(3, 2));
        Assert.Equal(key.ToLowerInvariant(), key);
        Assert.DoesNotContain('\\', key);
        Assert.False(key.StartsWith('/'));

        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        Assert.Equal(
            Path.Combine(root.Path, key.Replace('/', Path.DirectorySeparatorChar)),
            store.PathFor(hash));
    }

    // A name that is not a content address never becomes a path segment, because a hash arrives from a
    // manifest a remote peer may have written. The refusal is a throw rather than a null, because a caller
    // asking for a key has already decided to build a path out of the answer.
    [Fact]
    public void RelativeKeyFor_refuses_a_name_that_is_not_a_content_address()
    {
        Assert.Throws<ArgumentException>(() => FileSystemPackStore.RelativeKeyFor("../../etc/passwd"));
        Assert.Throws<ArgumentException>(() => FileSystemPackStore.RelativeKeyFor(new string('a', 63)));
        Assert.Throws<ArgumentException>(() => FileSystemPackStore.RelativeKeyFor(new string('A', 64)));
        Assert.Throws<ArgumentException>(() => FileSystemPackStore.RelativeKeyFor(string.Empty));
        Assert.Throws<ArgumentException>(() => FileSystemPackStore.RelativeKeyFor(null!));
    }

    [Fact]
    public async Task PutAsync_verifies_the_digest_of_the_bytes_it_was_handed()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();

        ContentPackException thrown = await Assert.ThrowsAsync<ContentPackException>(
            () => store.PutAsync(pack.ItemChunkZero.Hash, pack.TagChunk.StoredFile));

        Assert.Equal(pack.ItemChunkZero.Hash, thrown.Hash);
        Assert.Equal(ContentPackReader.ReasonHashMismatch, thrown.Reason);
        Assert.False(File.Exists(store.PathFor(pack.ItemChunkZero.Hash)));
        Assert.Empty(Directory.GetFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PutAsync_refuses_a_file_whose_magic_names_no_known_kind()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);

        ContentPackException thrown = await Assert.ThrowsAsync<ContentPackException>(
            () => store.PutAsync(new string('b', 64), new byte[] { 0x4B, 0x45, 0x43, 0x5A, 0x01 }));

        Assert.Equal(ContentPackReader.ReasonHashMismatch, thrown.Reason);
    }

    [Fact]
    public async Task PutAsync_of_a_hash_that_exists_is_a_no_op()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();

        await store.PutAsync(pack.TagChunk.Hash, pack.TagChunk.StoredFile);

        // Replacing the file's bytes out of band is how the no-op is OBSERVED: a second put that rewrote it
        // would restore the correct bytes and the assertion below would not be able to tell the two apart.
        string path = store.PathFor(pack.TagChunk.Hash);
        await File.WriteAllBytesAsync(path, [0x01, 0x02, 0x03]);

        await store.PutAsync(pack.TagChunk.Hash, pack.TagChunk.StoredFile);

        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task The_version_pointer_lands_outside_the_shard_tree()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();

        await pack.WriteAsync(store);

        string pointer = Path.Combine(root.Path, "versions", "7");
        Assert.Equal(pointer, store.VersionPointerPathFor(CatalogPack.VersionNumber));
        Assert.True(File.Exists(pointer));
        Assert.Empty(Directory.GetFiles(Path.Combine(root.Path, "versions"), "*.tmp"));

        PackVersionPointer? read = await store.GetVersionPointerAsync(CatalogPack.VersionNumber);
        Assert.NotNull(read);
        Assert.Equal(pack.ServerManifestHash, read.ServerManifestHash);
        Assert.Equal(pack.ClientManifestHash, read.ClientManifestHash);
    }

    [Fact]
    public async Task ListAsync_yields_every_hash_the_two_manifests_name_plus_both_manifests()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(store);

        List<string> listed = await Collect(store, CatalogPack.VersionNumber);

        Assert.Equal(pack.EveryHash().OrderBy(h => h, StringComparer.Ordinal), listed.OrderBy(h => h, StringComparer.Ordinal));
        Assert.Equal(listed.Count, listed.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ListAsync_does_not_walk_the_directory()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(store);

        // An orphan is a real file at a real content address that no manifest of this version names. A store
        // that answered by walking the tree would report it, and the publish sweep would then never delete it.
        byte[] orphan = ContentTextChunkCodec.Encode("fr-FR", [new KeyValuePair<string, string>("a", "b")]);
        string orphanHash = ContentTextChunkCodec.Hash("fr-FR", [new KeyValuePair<string, string>("a", "b")]);
        await store.PutAsync(orphanHash, orphan);

        List<string> listed = await Collect(store, CatalogPack.VersionNumber);

        Assert.DoesNotContain(orphanHash, listed);
        Assert.True(await store.ExistsAsync(orphanHash));
    }

    [Fact]
    public async Task ListAsync_yields_nothing_when_the_pointer_is_absent()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(store);

        Assert.Empty(await Collect(store, CatalogPack.VersionNumber + 1));
    }

    [Fact]
    public async Task ListAsync_yields_nothing_when_the_pointer_is_unreadable()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(store);

        await File.WriteAllTextAsync(store.VersionPointerPathFor(CatalogPack.VersionNumber), "not a hash\n", Encoding.UTF8);

        Assert.Empty(await Collect(store, CatalogPack.VersionNumber));
    }

    [Fact]
    public async Task ListAsync_yields_nothing_when_a_manifest_the_pointer_names_is_missing()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(store);

        // A partial list is the dangerous answer, because the sweep deletes everything OUTSIDE the keep set
        // and a keep set short of one manifest's chunks would take live files with it.
        File.Delete(store.PathFor(pack.ClientManifestHash));

        Assert.Empty(await Collect(store, CatalogPack.VersionNumber));
    }

    [Fact]
    public async Task The_durable_mode_writes_the_same_file_as_the_cheap_one()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path, PackDurability.PowerFail);
        CatalogPack pack = CatalogPack.Build();

        Assert.Equal(PackDurability.PowerFail, store.Durability);
        Assert.Equal(PackDurability.Default, new FileSystemPackStore(root.Path).Durability);

        await store.PutAsync(pack.TagChunk.Hash, pack.TagChunk.StoredFile);

        ReadOnlyMemory<byte>? read = await store.GetAsync(pack.TagChunk.Hash);
        Assert.NotNull(read);
        Assert.True(pack.TagChunk.StoredFile.Span.SequenceEqual(read.Value.Span));
    }

    [Fact]
    public async Task Pruning_enumerates_the_shard_tree_and_deletes_one_file_without_touching_a_pointer()
    {
        using var root = new TemporaryRoot();
        var store = new FileSystemPackStore(root.Path);
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(store);

        var onDisk = new List<string>();
        await foreach (string hash in ((IPackStorePruning)store).EnumerateAsync())
        {
            onDisk.Add(hash);
        }

        Assert.Equal(
            pack.EveryHash().OrderBy(h => h, StringComparer.Ordinal),
            onDisk.OrderBy(h => h, StringComparer.Ordinal));

        Assert.True(await ((IPackStorePruning)store).DeleteAsync(pack.TextChunkHash));
        Assert.False(await ((IPackStorePruning)store).DeleteAsync(pack.TextChunkHash));
        Assert.False(await store.ExistsAsync(pack.TextChunkHash));
        Assert.True(File.Exists(store.VersionPointerPathFor(CatalogPack.VersionNumber)));
    }

    [Fact]
    public void There_is_no_delete_on_the_common_store_interface()
    {
        // Pruning is a separate interface a provider MAY implement, so a read-only provider cannot be asked
        // to prune and a misconfigured one cannot delete a production pack through the common seam.
        string[] members = typeof(IPackStore).GetMethods().Select(m => m.Name).ToArray();
        Assert.DoesNotContain(members, m => m.Contains("Delete", StringComparison.Ordinal));
        Assert.Equal(4, members.Length);
    }

    static async Task<List<string>> Collect(IPackStore store, int versionNumber)
    {
        var listed = new List<string>();
        await foreach (string hash in store.ListAsync(versionNumber))
        {
            listed.Add(hash);
        }

        return listed;
    }
}
