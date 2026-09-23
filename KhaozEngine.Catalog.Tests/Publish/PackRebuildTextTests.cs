using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Store;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// The rebuild's TEXT guard: a version whose manifests would name text chunks is refused before anything is
/// built and before anything is written.
/// <para>
/// A manifest that names a language names a KECT text chunk hash, and <c>ContentPackRebuild</c> writes
/// chunks, the rule chunk, both manifests and the pointer, and no text chunk at all. The two manifest digests
/// cannot catch that, because both rebuilt manifests are built from the same language list the recorded ones
/// were and therefore name the same hashes either way. Without the guard the rebuild would report success,
/// write the pointer, and leave a pack whose boot fails fetching a text chunk nobody wrote.
/// </para>
/// <para>
/// Rebuilding the text instead of refusing has nothing to build on: no store keeps a VERSION's text, so there
/// is nothing to encode a text chunk from. Publishing text at all is
/// https://github.com/APKiwiOrg/KhaozEngine/issues/1000, and the language list every provider publishes today
/// is empty, which the shared conformance suite pins on each of them.
/// </para>
/// </summary>
public sealed class PackRebuildTextTests
{
    static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    /// <summary>
    /// The store answers a baseline naming one language, which is what a provider will answer the day text is
    /// publishable, and the rebuild refuses with <see cref="ContentPackRebuild.RefusedTextChunks"/> having
    /// written nothing at all.
    /// <para>
    /// The version under it was published with NO language, so its recorded digests do not cover the spliced
    /// one and the manifest comparison would refuse this rebuild too, as a mismatch. That is the strongest
    /// shape available while no store publishes text, and it still pins the thing that matters: the reason is
    /// the TEXT one, so the guard runs ahead of the comparison and cannot be out-ordered by it. In the case
    /// the guard exists for, a version published WITH text, the comparison matches and sees nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ARebuildOfAVersionWhoseManifestsWouldNameTextChunksIsRefusedBeforeAnyWrite()
    {
        using var rootA = new TemporaryRoot();
        using var rootB = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var packA = new FileSystemPackStore(rootA.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, packA);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(1)),
            ContentEdit.Add(Thing, new ContentKey("two"), PublishFixtures.Fields(2)));
        await store.PublishAsync(PublishFixtures.Request(0));

        ContentVersionRecord? record = await store.GetVersionAsync(1);
        Assert.NotNull(record);

        var speaking = new TextPublishingStore(store);
        var packB = new FileSystemPackStore(rootB.Path);
        ContentPackRebuildResult refused = await ContentPackRebuild.RunAsync(speaking, registry, 1, packB);

        Assert.False(refused.Rebuilt);
        Assert.Equal(ContentPackRebuild.RefusedTextChunks, refused.RefusalReason);
        Assert.NotNull(refused.RefusalDetail);
        Assert.Contains("version 1", refused.RefusalDetail, StringComparison.Ordinal);
        Assert.Contains("1 language", refused.RefusalDetail, StringComparison.Ordinal);

        // It refuses BEFORE the build, so it never claims to have encoded anything either.
        Assert.Equal(0, refused.ChunksBuilt);
        Assert.Equal(0, refused.ObjectsWritten);
        Assert.Equal(0L, refused.BytesWritten);

        // Nothing a reader could follow: no pointer, no manifest, and no object at all.
        Assert.Null(await packB.GetVersionPointerAsync(1));
        Assert.False(await packB.ExistsAsync(record.ServerManifestHash));
        Assert.False(await packB.ExistsAsync(record.ClientManifestHash));
        Assert.Empty(await EverythingAsync(packB));

        // The same store WITHOUT the language answers the same version fine, so the refusal is the language
        // list rather than anything the decorator did to the rows.
        ContentPackRebuildResult rebuilt = await ContentPackRebuild.RunAsync(store, registry, 1, packB);
        Assert.True(rebuilt.Rebuilt, rebuilt.RefusalDetail ?? rebuilt.RefusalReason);
    }

    static async Task<IReadOnlyList<string>> EverythingAsync(FileSystemPackStore store)
    {
        var hashes = new List<string>();
        await foreach (string hash in store.EnumerateAsync())
        {
            hashes.Add(hash);
        }

        return hashes;
    }
}

/// <summary>
/// An authoring store that answers a publish baseline naming ONE language, and delegates everything else.
/// <para>
/// It is the honest stand-in for the day a provider publishes text, because the language list is the only
/// thing the rebuild reads languages from and <see cref="ContentPublishBaseline"/> is public and constructed
/// here exactly as a provider constructs it. The alternative, reaching into the rebuild's own state, would
/// pin the refusal to a shape rather than to the fact that drives it.
/// </para>
/// </summary>
/// <param name="inner">The real store every other member is answered from.</param>
internal sealed class TextPublishingStore(IContentAuthoringStore inner) : IContentAuthoringStore
{
    /// <summary>The language the doctored baseline names, which is the fixtures' own tag.</summary>
    public const string LanguageTag = "en-US";

    static readonly KeyValuePair<string, string>[] Text = [new("thing.one.name", "One")];

    /// <inheritdoc />
    public IPackStore? PackStore => inner.PackStore;

    /// <inheritdoc />
    public Task InitializeAsync(ContentAuthoringSchemaMode mode, CancellationToken cancellationToken = default)
        => inner.InitializeAsync(mode, cancellationToken);

    /// <inheritdoc />
    public Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
        => inner.GetSchemaVersionAsync(cancellationToken);

    /// <inheritdoc />
    public Task<string> GetStoreEpochAsync(CancellationToken cancellationToken = default)
        => inner.GetStoreEpochAsync(cancellationToken);

    /// <inheritdoc />
    public Task<int> GetActiveVersionAsync(CancellationToken cancellationToken = default)
        => inner.GetActiveVersionAsync(cancellationToken);

    /// <inheritdoc />
    public Task<int?> GetPinnedVersionAsync(CancellationToken cancellationToken = default)
        => inner.GetPinnedVersionAsync(cancellationToken);

    /// <inheritdoc />
    public Task SetPinnedVersionAsync(
        int? version,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => inner.SetPinnedVersionAsync(version, actor, operatorId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentVersionRecord>> ListVersionsAsync(CancellationToken cancellationToken = default)
        => inner.ListVersionsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ContentVersionRecord?> GetVersionAsync(int versionNumber, CancellationToken cancellationToken = default)
        => inner.GetVersionAsync(versionNumber, cancellationToken);

    /// <inheritdoc />
    public Task<ContentSnapshot> LoadSnapshotAsync(
        int versionNumber,
        ContentTypeRegistry registry,
        CancellationToken cancellationToken = default)
        => inner.LoadSnapshotAsync(versionNumber, registry, cancellationToken);

    /// <inheritdoc />
    public Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
        => inner.GetOpenDraftAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ContentDraft> ApplyEditsAsync(
        IReadOnlyList<ContentEdit> edits,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
        => inner.ApplyEditsAsync(edits, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public Task DiscardDraftAsync(string actor, string operatorId, CancellationToken cancellationToken = default)
        => inner.DiscardDraftAsync(actor, operatorId, cancellationToken);

    /// <inheritdoc />
    public Task FreezeDraftAsync(int baseVersion, CancellationToken cancellationToken = default)
        => inner.FreezeDraftAsync(baseVersion, cancellationToken);

    /// <inheritdoc />
    public Task ClearDraftFreezeAsync(CancellationToken cancellationToken = default)
        => inner.ClearDraftFreezeAsync(cancellationToken);

    /// <summary>
    /// The real baseline with ONE language spliced into it, at a real KECT content address, so the rebuild
    /// reads exactly what it would read off a provider that had published a text chunk.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<ContentPublishBaseline> ReadPublishBaselineAsync(CancellationToken cancellationToken = default)
    {
        ContentPublishBaseline baseline = await inner
            .ReadPublishBaselineAsync(cancellationToken).ConfigureAwait(false);

        return new ContentPublishBaseline(
            baseline.VersionNumber,
            baseline.Rows,
            baseline.Rules,
            baseline.Chunks,
            [new ManifestLanguageEntry(LanguageTag, ContentTextChunkCodec.Hash(LanguageTag, Text))],
            baseline.MinimumServerBuild,
            baseline.MinimumClientBuild);
    }

    /// <inheritdoc />
    public Task<ContentVersionRecord> CommitPublishAsync(
        ContentPublishPlan plan,
        ContentPublishRequest request,
        IPackVersionPointerStore? pointers,
        CancellationToken cancellationToken = default)
        => inner.CommitPublishAsync(plan, request, pointers, cancellationToken);

    /// <inheritdoc />
    public Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
        => inner.PublishAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<ContentDraft> RollbackToAsync(
        int targetVersion,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
        => inner.RollbackToAsync(targetVersion, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public Task<ContentRowPage> ListRowsAsync(
        ContentTypeId type,
        int versionNumber,
        string? keyPrefix,
        bool includeRetired,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
        => inner.ListRowsAsync(type, versionNumber, keyPrefix, includeRetired, skip, take, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentRowRevision>> GetRowHistoryAsync(
        ContentTypeId type,
        int definitionId,
        CancellationToken cancellationToken = default)
        => inner.GetRowHistoryAsync(type, definitionId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentAuditEntry>> ListAuditAsync(
        ContentTypeId type,
        int definitionId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
        => inner.ListAuditAsync(type, definitionId, skip, take, cancellationToken);

    /// <inheritdoc />
    public Task AppendOperationalAuditAsync(
        string action,
        string actor,
        string operatorId,
        string fieldName,
        string? value,
        string note,
        CancellationToken cancellationToken = default)
        => inner.AppendOperationalAuditAsync(
            action, actor, operatorId, fieldName, value, note, cancellationToken);

    /// <inheritdoc />
    public Task<int> AllocateAsync(ContentTypeId type, int count, CancellationToken cancellationToken = default)
        => inner.AllocateAsync(type, count, cancellationToken);

    /// <inheritdoc />
    public Task<int> AllocateInFamilyAsync(long familyId, CancellationToken cancellationToken = default)
        => inner.AllocateInFamilyAsync(familyId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentFamily>> ListFamiliesAsync(
        ContentTypeId type,
        CancellationToken cancellationToken = default)
        => inner.ListFamiliesAsync(type, cancellationToken);

    /// <inheritdoc />
    public Task<ContentFamily> CreateFamilyAsync(
        ContentTypeId type,
        string familyKey,
        int blockSize,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => inner.CreateFamilyAsync(type, familyKey, blockSize, actor, operatorId, cancellationToken);

    /// <inheritdoc />
    public Task<ContentPublishResult> ImportBundleAsync(
        ContentBundle bundle,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
        => inner.ImportBundleAsync(bundle, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public Task<ContentBundle> ExportBundleAsync(int versionNumber, CancellationToken cancellationToken = default)
        => inner.ExportBundleAsync(versionNumber, cancellationToken);
}
