using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.ConditionalFreeze;

/// <summary>
/// A store that implements only the released authoring seam, forwarding every member to a real store and recording
/// every call by name, reads and the release included. It declares no text companion, no upgrade ledger and no
/// guarded freeze, which is the shape of a custom provider built before the guarded freeze existed.
/// <para>
/// <b>It is independent of every other test double</b>, so it inherits no capability by accident, and a test can
/// say exactly which members a refused publish reached: none.
/// </para>
/// </summary>
/// <param name="inner">The real store every member is answered from.</param>
internal sealed class LegacyFreezeStoreView(IContentAuthoringStore inner) : IContentAuthoringStore
{
    readonly List<string> _calls = [];

    /// <summary>Every member called, in order, by name.</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_calls)
            {
                return _calls.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public IPackStore? PackStore
    {
        get
        {
            Record(nameof(PackStore));
            return inner.PackStore;
        }
    }

    /// <inheritdoc />
    public Task InitializeAsync(ContentAuthoringSchemaMode mode, CancellationToken cancellationToken = default)
    {
        Record(nameof(InitializeAsync));
        return inner.InitializeAsync(mode, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        Record(nameof(GetSchemaVersionAsync));
        return inner.GetSchemaVersionAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<string> GetStoreEpochAsync(CancellationToken cancellationToken = default)
    {
        Record(nameof(GetStoreEpochAsync));
        return inner.GetStoreEpochAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> GetActiveVersionAsync(CancellationToken cancellationToken = default)
    {
        Record(nameof(GetActiveVersionAsync));
        return inner.GetActiveVersionAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<int?> GetPinnedVersionAsync(CancellationToken cancellationToken = default)
    {
        Record(nameof(GetPinnedVersionAsync));
        return inner.GetPinnedVersionAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task SetPinnedVersionAsync(
        int? version, string actor, string operatorId, CancellationToken cancellationToken = default)
    {
        Record(nameof(SetPinnedVersionAsync));
        return inner.SetPinnedVersionAsync(version, actor, operatorId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentVersionRecord>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        Record(nameof(ListVersionsAsync));
        return inner.ListVersionsAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentVersionRecord?> GetVersionAsync(int versionNumber, CancellationToken cancellationToken = default)
    {
        Record(nameof(GetVersionAsync));
        return inner.GetVersionAsync(versionNumber, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentSnapshot> LoadSnapshotAsync(
        int versionNumber, ContentTypeRegistry registry, CancellationToken cancellationToken = default)
    {
        Record(nameof(LoadSnapshotAsync));
        return inner.LoadSnapshotAsync(versionNumber, registry, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
    {
        Record(nameof(GetOpenDraftAsync));
        return inner.GetOpenDraftAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentDraft> ApplyEditsAsync(
        IReadOnlyList<ContentEdit> edits,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(ApplyEditsAsync));
        return inner.ApplyEditsAsync(edits, actor, operatorId, note, cancellationToken);
    }

    /// <inheritdoc />
    public Task DiscardDraftAsync(string actor, string operatorId, CancellationToken cancellationToken = default)
    {
        Record(nameof(DiscardDraftAsync));
        return inner.DiscardDraftAsync(actor, operatorId, cancellationToken);
    }

    /// <inheritdoc />
    public Task FreezeDraftAsync(int baseVersion, CancellationToken cancellationToken = default)
    {
        Record(nameof(FreezeDraftAsync));
        return inner.FreezeDraftAsync(baseVersion, cancellationToken);
    }

    /// <inheritdoc />
    public Task ClearDraftFreezeAsync(CancellationToken cancellationToken = default)
    {
        Record(nameof(ClearDraftFreezeAsync));
        return inner.ClearDraftFreezeAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentPublishBaseline> ReadPublishBaselineAsync(CancellationToken cancellationToken = default)
    {
        Record(nameof(ReadPublishBaselineAsync));
        return inner.ReadPublishBaselineAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentVersionRecord> CommitPublishAsync(
        ContentPublishPlan plan,
        ContentPublishRequest request,
        IPackVersionPointerStore? pointers,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(CommitPublishAsync));
        return inner.CommitPublishAsync(plan, request, pointers, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request, CancellationToken cancellationToken = default)
    {
        Record(nameof(PublishAsync));
        return inner.PublishAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentDraft> RollbackToAsync(
        int targetVersion, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
    {
        Record(nameof(RollbackToAsync));
        return inner.RollbackToAsync(targetVersion, actor, operatorId, note, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentRowPage> ListRowsAsync(
        ContentTypeId type,
        int versionNumber,
        string? keyPrefix,
        bool includeRetired,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(ListRowsAsync));
        return inner.ListRowsAsync(type, versionNumber, keyPrefix, includeRetired, skip, take, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentRowRevision>> GetRowHistoryAsync(
        ContentTypeId type, int definitionId, CancellationToken cancellationToken = default)
    {
        Record(nameof(GetRowHistoryAsync));
        return inner.GetRowHistoryAsync(type, definitionId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentAuditEntry>> ListAuditAsync(
        ContentTypeId type, int definitionId, int skip, int take, CancellationToken cancellationToken = default)
    {
        Record(nameof(ListAuditAsync));
        return inner.ListAuditAsync(type, definitionId, skip, take, cancellationToken);
    }

    /// <inheritdoc />
    public Task AppendOperationalAuditAsync(
        string action,
        string actor,
        string operatorId,
        string fieldName,
        string? value,
        string note,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(AppendOperationalAuditAsync));
        return inner.AppendOperationalAuditAsync(action, actor, operatorId, fieldName, value, note, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> AllocateAsync(ContentTypeId type, int count, CancellationToken cancellationToken = default)
    {
        Record(nameof(AllocateAsync));
        return inner.AllocateAsync(type, count, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> AllocateInFamilyAsync(long familyId, CancellationToken cancellationToken = default)
    {
        Record(nameof(AllocateInFamilyAsync));
        return inner.AllocateInFamilyAsync(familyId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentFamily>> ListFamiliesAsync(
        ContentTypeId type, CancellationToken cancellationToken = default)
    {
        Record(nameof(ListFamiliesAsync));
        return inner.ListFamiliesAsync(type, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentFamily> CreateFamilyAsync(
        ContentTypeId type,
        string familyKey,
        int blockSize,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(CreateFamilyAsync));
        return inner.CreateFamilyAsync(type, familyKey, blockSize, actor, operatorId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentPublishResult> ImportBundleAsync(
        ContentBundle bundle, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
    {
        Record(nameof(ImportBundleAsync));
        return inner.ImportBundleAsync(bundle, actor, operatorId, note, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentBundle> ExportBundleAsync(int versionNumber, CancellationToken cancellationToken = default)
    {
        Record(nameof(ExportBundleAsync));
        return inner.ExportBundleAsync(versionNumber, cancellationToken);
    }

    void Record(string member)
    {
        lock (_calls)
        {
            _calls.Add(member);
        }
    }
}
