using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// Every member of <see cref="IContentAuthoringStore"/> forwarded to an inner store, so a double overrides
/// the baseline member under test while preserving every other operation.
/// <para>
/// It declares the guarded freeze companion, so its constructor requires an inner store that declares it and
/// both companion members forward there. Every engine store does.
/// </para>
/// </summary>
internal abstract class ForwardingContentAuthoringStore(IContentAuthoringStore inner)
    : IContentAuthoringStore, IContentConditionalDraftFreeze
{
    readonly IContentConditionalDraftFreeze _guarded = inner as IContentConditionalDraftFreeze
        ?? throw new ArgumentException("a forwarding store's inner store must declare the guarded freeze companion.", nameof(inner));

    /// <summary>The store behind the decorator, which a double also reads directly.</summary>
    protected IContentAuthoringStore Inner => inner;

    /// <inheritdoc />
    public IPackStore? PackStore => inner.PackStore;

    /// <inheritdoc />
    public virtual Task InitializeAsync(ContentAuthoringSchemaMode mode, CancellationToken cancellationToken = default)
        => inner.InitializeAsync(mode, cancellationToken);

    /// <inheritdoc />
    public virtual Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
        => inner.GetSchemaVersionAsync(cancellationToken);

    /// <inheritdoc />
    public virtual Task<string> GetStoreEpochAsync(CancellationToken cancellationToken = default)
        => inner.GetStoreEpochAsync(cancellationToken);

    /// <inheritdoc />
    public virtual Task<int> GetActiveVersionAsync(CancellationToken cancellationToken = default)
        => inner.GetActiveVersionAsync(cancellationToken);

    /// <inheritdoc />
    public virtual Task<int?> GetPinnedVersionAsync(CancellationToken cancellationToken = default)
        => inner.GetPinnedVersionAsync(cancellationToken);

    /// <inheritdoc />
    public virtual Task SetPinnedVersionAsync(
        int? version,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => inner.SetPinnedVersionAsync(version, actor, operatorId, cancellationToken);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<ContentVersionRecord>> ListVersionsAsync(
        CancellationToken cancellationToken = default)
        => inner.ListVersionsAsync(cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentVersionRecord?> GetVersionAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
        => inner.GetVersionAsync(versionNumber, cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentSnapshot> LoadSnapshotAsync(
        int versionNumber,
        ContentTypeRegistry registry,
        CancellationToken cancellationToken = default)
        => inner.LoadSnapshotAsync(versionNumber, registry, cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
        => inner.GetOpenDraftAsync(cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentDraft> ApplyEditsAsync(
        IReadOnlyList<ContentEdit> edits,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
        => inner.ApplyEditsAsync(edits, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public virtual Task DiscardDraftAsync(
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => inner.DiscardDraftAsync(actor, operatorId, cancellationToken);

    /// <inheritdoc />
    public virtual Task FreezeDraftAsync(int baseVersion, CancellationToken cancellationToken = default)
        => inner.FreezeDraftAsync(baseVersion, cancellationToken);

    /// <inheritdoc />
    public virtual Task ClearDraftFreezeAsync(CancellationToken cancellationToken = default)
        => inner.ClearDraftFreezeAsync(cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentDraft> FreezeDraftForBaseAsync(
        int expectedBaseVersion,
        CancellationToken cancellationToken = default)
        => _guarded.FreezeDraftForBaseAsync(expectedBaseVersion, cancellationToken);

    /// <inheritdoc />
    public virtual Task<bool> ReleaseDraftFreezeForBaseAsync(
        int frozenForBaseVersion,
        CancellationToken cancellationToken = default)
        => _guarded.ReleaseDraftFreezeForBaseAsync(frozenForBaseVersion, cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentPublishBaseline> ReadPublishBaselineAsync(CancellationToken cancellationToken = default)
        => inner.ReadPublishBaselineAsync(cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentVersionRecord> CommitPublishAsync(
        ContentPublishPlan plan,
        ContentPublishRequest request,
        IPackVersionPointerStore? pointers,
        CancellationToken cancellationToken = default)
        => inner.CommitPublishAsync(plan, request, pointers, cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
        => inner.PublishAsync(request, cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentDraft> RollbackToAsync(
        int targetVersion,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
        => inner.RollbackToAsync(targetVersion, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentRowPage> ListRowsAsync(
        ContentTypeId type,
        int versionNumber,
        string? keyPrefix,
        bool includeRetired,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
        => inner.ListRowsAsync(type, versionNumber, keyPrefix, includeRetired, skip, take, cancellationToken);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<ContentRowRevision>> GetRowHistoryAsync(
        ContentTypeId type,
        int definitionId,
        CancellationToken cancellationToken = default)
        => inner.GetRowHistoryAsync(type, definitionId, cancellationToken);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<ContentAuditEntry>> ListAuditAsync(
        ContentTypeId type,
        int definitionId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
        => inner.ListAuditAsync(type, definitionId, skip, take, cancellationToken);

    /// <inheritdoc />
    public virtual Task AppendOperationalAuditAsync(
        string action,
        string actor,
        string operatorId,
        string fieldName,
        string? value,
        string note,
        CancellationToken cancellationToken = default)
        => inner.AppendOperationalAuditAsync(action, actor, operatorId, fieldName, value, note, cancellationToken);

    /// <inheritdoc />
    public virtual Task<int> AllocateAsync(
        ContentTypeId type,
        int count,
        CancellationToken cancellationToken = default)
        => inner.AllocateAsync(type, count, cancellationToken);

    /// <inheritdoc />
    public virtual Task<int> AllocateInFamilyAsync(long familyId, CancellationToken cancellationToken = default)
        => inner.AllocateInFamilyAsync(familyId, cancellationToken);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<ContentFamily>> ListFamiliesAsync(
        ContentTypeId type,
        CancellationToken cancellationToken = default)
        => inner.ListFamiliesAsync(type, cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentFamily> CreateFamilyAsync(
        ContentTypeId type,
        string familyKey,
        int blockSize,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => inner.CreateFamilyAsync(type, familyKey, blockSize, actor, operatorId, cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentPublishResult> ImportBundleAsync(
        ContentBundle bundle,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
        => inner.ImportBundleAsync(bundle, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentBundle> ExportBundleAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
        => inner.ExportBundleAsync(versionNumber, cancellationToken);
}
