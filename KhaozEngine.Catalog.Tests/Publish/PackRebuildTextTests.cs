using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Store;
using Xunit;
using static KhaozEngine.Tests.Catalog.Publish.TextPublishFixtures;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// The rebuild's CAPABILITY guard. A version whose manifests name text chunks can only be rebuilt through a
/// store that reads that exact version's text through the companion. A store exposing the row-only seam
/// alone is refused before anything is built or written, and the same version rebuilds whole through the
/// companion, text chunks included.
/// </summary>
public sealed class PackRebuildTextTests
{
    /// <summary>
    /// A real text version, published through the companion, read back through a ROW-ONLY view of the same
    /// store. The view's baseline names the language, and with no way to read version 1's values the rebuild
    /// refuses with <see cref="ContentPackRebuild.RefusedTextChunks"/> having written nothing. The reference
    /// store itself rebuilds the version, writing its text chunk before the manifests and the pointer.
    /// </summary>
    [Fact]
    public async Task A_text_version_through_a_store_without_the_companion_is_refused_before_any_write()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        var packA = new FileSystemPackStore(rootA.Path);
        InMemoryContentAuthoringStore store = TextAuthoringFixtures.TextStore(packA);
        await TextAuthoringFixtures.ApplyAsync(store, new[] { TextAuthoringFixtures.Add() }, Set("sword", "en", "Sword"));
        ContentVersionRecord record = await PublishAsync(store);

        var rowOnly = new RowOnlyStoreView(store);
        var packB = new FileSystemPackStore(rootB.Path);
        ContentPackRebuildResult refused = await ContentPackRebuild.RunAsync(
            rowOnly, TextAuthoringFixtures.TextRegistry(), 1, packB);

        Assert.False(refused.Rebuilt);
        Assert.Equal(ContentPackRebuild.RefusedTextChunks, refused.RefusalReason);
        Assert.NotNull(refused.RefusalDetail);
        Assert.Contains("version 1", refused.RefusalDetail, StringComparison.Ordinal);
        Assert.Contains("1 language", refused.RefusalDetail, StringComparison.Ordinal);
        Assert.Equal(0, refused.ChunksBuilt);
        Assert.Equal(0, refused.ObjectsWritten);
        Assert.Equal(0L, refused.BytesWritten);
        Assert.Null(await packB.GetVersionPointerAsync(1));
        Assert.False(await packB.ExistsAsync(record.ServerManifestHash));
        Assert.False(await packB.ExistsAsync(record.ClientManifestHash));
        Assert.Empty(await ObjectsAsync(packB));
        Assert.Empty(rowOnly.Calls);

        ContentPackRebuildResult rebuilt = await ContentPackRebuild.RunAsync(
            store, TextAuthoringFixtures.TextRegistry(), 1, packB);
        Assert.True(rebuilt.Rebuilt, rebuilt.RefusalDetail ?? rebuilt.RefusalReason);
        Assert.True(await packB.ExistsAsync(Hash("en", (SwordName, "Sword"))));
        Assert.NotNull(await packB.GetVersionPointerAsync(1));
    }
}
