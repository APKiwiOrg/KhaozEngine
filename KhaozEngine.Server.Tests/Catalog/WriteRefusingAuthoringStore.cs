using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// A store whose every WRITE member records its own name and throws, and whose every read forwards, which is
/// what proves an action set writes nothing: a handler that reached a write would fail loudly even if it caught
/// the throw, because the attempt is recorded before it.
/// <para>
/// A write is any member that can change what the store holds, including the two whose names read as reads:
/// <see cref="IContentAuthoringStore.ReadPublishBaselineAsync"/>, which clears a stale freeze marker, and
/// <see cref="IContentAuthoringStore.InitializeAsync"/>, which creates the schema under auto create. The pack
/// the store publishes into is wrapped the same way, so a put through <see cref="PackStore"/> is refused too.
/// </para>
/// </summary>
/// <param name="inner">The store the reads forward to.</param>
internal sealed class WriteRefusingAuthoringStore(IContentAuthoringStore inner) : IContentAuthoringStore
{
    readonly List<string> _attempts = [];

    /// <summary>Every write member reached, in call order, which a test asserts is empty.</summary>
    public IReadOnlyList<string> WriteAttempts => _attempts;

    /// <inheritdoc />
    public IPackStore? PackStore => inner.PackStore is IPackStore pack ? new WriteRefusingPack(pack, this) : null;

    /// <inheritdoc />
    public Task InitializeAsync(ContentAuthoringSchemaMode mode, CancellationToken cancellationToken = default)
        => throw Refuse();

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
        => throw Refuse();

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentVersionRecord>> ListVersionsAsync(
        CancellationToken cancellationToken = default)
        => inner.ListVersionsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ContentVersionRecord?> GetVersionAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
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
        => throw Refuse();

    /// <inheritdoc />
    public Task DiscardDraftAsync(
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => throw Refuse();

    /// <inheritdoc />
    public Task FreezeDraftAsync(int baseVersion, CancellationToken cancellationToken = default)
        => throw Refuse();

    /// <inheritdoc />
    public Task ClearDraftFreezeAsync(CancellationToken cancellationToken = default)
        => throw Refuse();

    /// <inheritdoc />
    public Task<ContentPublishBaseline> ReadPublishBaselineAsync(CancellationToken cancellationToken = default)
        => throw Refuse();

    /// <inheritdoc />
    public Task<ContentVersionRecord> CommitPublishAsync(
        ContentPublishPlan plan,
        ContentPublishRequest request,
        IPackVersionPointerStore? pointers,
        CancellationToken cancellationToken = default)
        => throw Refuse();

    /// <inheritdoc />
    public Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
        => throw Refuse();

    /// <inheritdoc />
    public Task<ContentDraft> RollbackToAsync(
        int targetVersion,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
        => throw Refuse();

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
        => throw Refuse();

    /// <inheritdoc />
    public Task<int> AllocateAsync(
        ContentTypeId type,
        int count,
        CancellationToken cancellationToken = default)
        => throw Refuse();

    /// <inheritdoc />
    public Task<int> AllocateInFamilyAsync(long familyId, CancellationToken cancellationToken = default)
        => throw Refuse();

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
        => throw Refuse();

    /// <inheritdoc />
    public Task<ContentPublishResult> ImportBundleAsync(
        ContentBundle bundle,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
        => throw Refuse();

    /// <inheritdoc />
    public Task<ContentBundle> ExportBundleAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
        => inner.ExportBundleAsync(versionNumber, cancellationToken);

    InvalidOperationException Refuse([CallerMemberName] string member = "")
    {
        _attempts.Add(member);
        return new InvalidOperationException("A read-only action set reached the store write member " + member + ".");
    }

    /// <summary>The store's pack with its one write refused through the owning store's record.</summary>
    /// <param name="pack">The pack the reads forward to.</param>
    /// <param name="owner">The store whose record a refused put lands in.</param>
    sealed class WriteRefusingPack(IPackStore pack, WriteRefusingAuthoringStore owner) : IPackStore
    {
        /// <inheritdoc />
        public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
            => pack.ExistsAsync(hash, cancellationToken);

        /// <inheritdoc />
        public Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
            => pack.GetAsync(hash, cancellationToken);

        /// <inheritdoc />
        public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
            => throw owner.Refuse("IPackStore.PutAsync");

        /// <inheritdoc />
        public IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken cancellationToken = default)
            => pack.ListAsync(versionNumber, cancellationToken);
    }
}
